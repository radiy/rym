using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Inflector;
using log4net;
using log4net.Config;
using NDesk.Options;

namespace Rym
{
	public class RymApp
	{
		private CancellationTokenSource source;

		protected ILog Log;
		protected bool Debug;
		protected bool Trace;

		protected CancellationToken Cancellation;

		private static string app;

		public string DefaultType;
		public static string DefaultMethod;

		static RymApp()
		{
			app = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName).ToLower();
		}

		public RymApp()
		{
			Log = LogManager.GetLogger(GetType());
			source = new CancellationTokenSource();
			Cancellation = source.Token;
		}

		public static int Run<T>(string[] args) where T : RymApp, new()
		{
			var version = false;
			var help = false;
			T appObject = null;
			try {
				appObject = new T();
				Console.CancelKeyPress += (sender, eventArgs) => {
					try {
						appObject.source.Cancel();
					}
					catch(Exception e) {
						appObject.PrintError(e);
					}
				};
				if (ConfigurationManager.GetSection("log4net") != null)
					XmlConfigurator.Configure();

				var vars = Environment.GetEnvironmentVariables();
				if (vars.Contains("TERM") && Equals(vars["TERM"], "cygwin")) {
					Console.OutputEncoding = Encoding.UTF8;
				}

				var options = new OptionSet {
					{"debug", v => appObject.Debug = v != null},
					{"trace", v => appObject.Trace = v != null},
					{"help", v => help = v != null},
					{"version", v => version = v != null},
				};
				var cliArgs = options.Parse(args);
				var types = new [] {typeof(T)};
				if (help) {
					var assembly = typeof(T).Assembly;
					var description = assembly.GetCustomAttributes(typeof(AssemblyDescriptionAttribute), false)
						.OfType<AssemblyDescriptionAttribute>()
						.Select(a => a.Description)
						.FirstOrDefault();
					if (!String.IsNullOrEmpty(description))
						Console.WriteLine(description);
					options.WriteOptionDescriptions(Console.Out);
					var tuples = (from t in types
						from m in t.GetMethods()
						where m.DeclaringType == t
						select Tuple.Create(t, m))
						.ToArray();
					Help(options, tuples);
					return 0;
				}
				if (version) {
					var assembly = typeof(T).Assembly;
					Console.WriteLine(assembly.GetName().Version.ToString());
					var hash = assembly.GetCustomAttributes(typeof(AssemblyCopyrightAttribute), false)
						.OfType<AssemblyCopyrightAttribute>()
						.Select(a => a.Copyright)
						.FirstOrDefault();
					Console.WriteLine(hash);
					return 0;
				}
				var action = GetAction(options, types, cliArgs, new string[0], appObject);
				if (action != null)
					action();
				return 0;
			}
			catch (Exception e) {
				if (appObject != null) {
					appObject.PrintError(e);
				} else {
					PrintErrorFallback(e);
				}
				return 1;
			}
		}

		protected virtual void PrintError(Exception e)
		{
			var origin = Console.ForegroundColor;
			try {
				Console.ForegroundColor = ConsoleColor.Red;
				if (e is TargetInvocationException)
					e = e.InnerException ?? e;

				if (e is ReflectionTypeLoadException) {
					foreach (var le in ((ReflectionTypeLoadException)e).LoaderExceptions) {
						PrintError(le);
					}
				} else {
					if (Trace)
						Console.Error.Write(e);
					else {
						Console.Error.Write(e.Message);
					}
				}
			}
			finally {
				Console.ForegroundColor = origin;
			}
		}

		private static void PrintErrorFallback(Exception e)
		{
			var origin = Console.ForegroundColor;
			try {
				Console.ForegroundColor = ConsoleColor.Red;
				if (e is ReflectionTypeLoadException) {
					foreach (var le in ((ReflectionTypeLoadException)e).LoaderExceptions) {
						PrintErrorFallback(le);
					}
				} else {
					Console.Error.Write(e);
				}
			}
			finally {
				Console.ForegroundColor = origin;
			}
		}

		public static bool ReadArguments(Tuple<Type, MethodInfo> runTuple, List<string> cliArgs, out object[] values)
		{
			var parameters = runTuple.Item2.GetParameters();
			values = new object[parameters.Length];
			var dummy = values;
			var options = GetOptions(parameters, values);

			var positioned = options.Parse(cliArgs);
			var notFilled = parameters.Select((j, p) => Tuple.Create(j, p))
				.Where(p => !p.Item1.HasDefaultValue && dummy[p.Item2] == null)
				.ToArray();
			if (positioned.Count != notFilled.Length) {
				Console.WriteLine("Не заполнены обязательные параметры");
				options.WriteOptionDescriptions(Console.Out);
				return true;
			}
			for (int i = 0; i < positioned.Count; i++) {
				values[notFilled[i].Item2] = TypeDescriptor.GetConverter(parameters[i].ParameterType)
					.ConvertFromString(positioned[i]);
			}

			if (parameters.Where(p => !p.HasDefaultValue).Select((p, i) => dummy[i]).Any(v => v == null)) {
				Console.WriteLine("Не заданы параметры для запуска задачи");
				options.WriteOptionDescriptions(Console.Out);
				return true;
			}
			return false;
		}

		public static void DescribeTask(Tuple<Type, MethodInfo> tuple)
		{
			var method = tuple.Item2;
			var type = tuple.Item1;
			var name = method.Name;
			if (name == DefaultMethod)
				name = "";
			Console.Write("  {0} {1} {2}",
				app,
				type.Name.Underscore().Dasherize(),
				name.Underscore().Dasherize());
			var mandatory = method.GetParameters().Where(p => !p.HasDefaultValue).ToArray();
			if (mandatory.Length > 0) {
				Console.Write(" " + String.Join(" ", mandatory.Select(p => "{" + p.Name + "}")));
			}
			var attr = method.GetCustomAttribute<DescriptionAttribute>();
			if (attr != null) {
				Console.Write("  # " + attr.Description);
			}
			Console.WriteLine();
		}

		public static void DescribeTasks(IEnumerable<Tuple<Type, MethodInfo>> tuples)
		{
			foreach (var tuple in tuples) {
				DescribeTask(tuple);
			}
		}

		public static void ShortHelp(Tuple<Type, MethodInfo>[] tuples, OptionSet options)
		{
			Console.WriteLine("Использование: [ОПЦИИ] [ЗАДАЧА] [ПОДЗАДАЧА] [АРГУМЕНТЫ]");
			Console.WriteLine("Для получения подробной информации о задаче - '{0} help {{ЗАДАЧА}} [ПОДЗАДАЧА]'", app);
			Console.WriteLine("Опции:");
			options.WriteOptionDescriptions(Console.Out);
			Console.WriteLine("Задачи:");
			Console.WriteLine("  {0} help [ЗАДАЧА] [ПОДЗАДАЧА] # Выводит информацию о всех задачах или о заданной задаче", app);
			DescribeTasks(tuples);
			Console.WriteLine("Аргументы:");
			Console.WriteLine("  параметры для задачи могут передаваться в двух формах:");
			Console.WriteLine("    позиционной - ЗАДАЧА ПАРАМЕТР1 ПАРАМЕТР2");
			Console.WriteLine("    не позиционной - ЗАДАЧА --ИМЯ-ПАРАМЕТРА1=ПАРАМЕТР1 --ИМЯ-ПАРАМЕТРА2=ПАРАМЕТР2");
			Console.WriteLine("    или смешанной ЗАДАЧА --ИМЯ-ПАРАМЕТРА1=ПАРАМЕТР1 ПАРАМЕТР2");
			Console.WriteLine("    параметры в фигурных скобках являются обязательными - {ПАРАМЕТР1}");
			Console.WriteLine("    параметры в квадратных скобках являются опциональными - [ПАРАМЕТР1]");
		}

		public static void LongDescribe(Tuple<Type, MethodInfo> tuple)
		{
			var values = new object[tuple.Item2.GetParameters().Length];
			DescribeTask(tuple);
			var options = GetOptions(tuple.Item2.GetParameters(), values);
			options.WriteOptionDescriptions(Console.Out);
		}

		public static OptionSet GetOptions(ParameterInfo[] parameters, object[] values)
		{
			var options = new OptionSet();
			for (var i = 0; i < parameters.Length; i++) {
				var parameter = parameters[i];
				if (parameter.HasDefaultValue)
					values[i] = parameter.RawDefaultValue;

				var scoped = i;
				var name = parameter.Name.Underscore().Dasherize();
				var sufix = parameter.HasDefaultValue ? ":" : "=";
				if (parameter.ParameterType == typeof(bool))
					sufix = "";

				var desc = "";
				var descAttr = parameter.GetCustomAttribute<DescriptionAttribute>();
				if (descAttr != null)
					desc = descAttr.Description;

				options.Add(name + sufix, desc, v => {
					if (parameter.ParameterType == typeof(bool))
						values[scoped] = v != null;
					else
						values[scoped] = TypeDescriptor.GetConverter(parameter.ParameterType).ConvertFromString(v);
				});
			}
			return options;
		}

		public static void Help(OptionSet rootOptions, Tuple<Type, MethodInfo>[] tuples, Tuple<Type, MethodInfo> runTuple = null)
		{
			if (runTuple == null) {
				ShortHelp(tuples, rootOptions);
			}
			else {
				LongDescribe(runTuple);
				if (runTuple.Item2.Name == DefaultMethod) {
					DescribeTasks(
						tuples.Where(t => t.Item1 == runTuple.Item1 && !t.Equals(runTuple)));
				}
			}
		}

		public static Action GetAction(OptionSet rootOptions,
			IEnumerable<Type> types,
			List<string> cliArgs,
			string[] ignoreMethods,
			object instance = null)
		{
			cliArgs = cliArgs.Where(a => !String.IsNullOrWhiteSpace(a)).ToList();
			var originArgs = cliArgs.ToArray();
			var tuples = (from t in types
				from m in t.GetMethods()
				where m.DeclaringType == t && !ignoreMethods.Contains(m.Name)
				select Tuple.Create(t, m))
				.ToArray();

			var isHelp = (cliArgs.FirstOrDefault() ?? "help") == "help";
			if (isHelp)
				Consume(cliArgs);
			var typeName = Consume(cliArgs);
			var methodName = (cliArgs.FirstOrDefault() ?? "").Underscore().Dasherize();
			var defaultMethodName = (DefaultMethod ?? "").Underscore().Dasherize();
			var runTuple = tuples.FirstOrDefault(t => t.Item1.Name.Underscore().Dasherize() == typeName
				&& (t.Item2.Name.Underscore().Dasherize() == methodName
					|| t.Item2.Name.Underscore().Dasherize() == defaultMethodName));

			if (runTuple != null && runTuple.Item2.Name.Underscore().Dasherize() == methodName)
				Consume(cliArgs);

			if (isHelp) {
				Help(rootOptions, tuples, runTuple);
				return null;
			}

			if (runTuple == null) {
				Console.WriteLine(
					"Не смог найти задачу {0}, попробуй help что бы посмотреть все доступные задачи",
					String.Join(" ", originArgs));
				return null;
			}

			object[] values;
			if (ReadArguments(runTuple, cliArgs, out values))
				return null;
			return () => {
				var item = instance ?? Activator.CreateInstance(runTuple.Item1);
				runTuple.Item2.Invoke(item, values);
				var disposable = item as IDisposable;
				if (disposable != null)
					disposable.Dispose();
			};
		}

		public static string Consume(List<string> args)
		{
			var value = args.FirstOrDefault();
			if (args.Count > 0)
				args.RemoveAt(0);
			return value;
		}
	}
}
