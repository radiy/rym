using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Configuration;
using System.Configuration.Internal;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using GlobDir;
using Inflector;
using NDesk.Options;
using Rym;

namespace rym
{
	public class StubConfigSystem : IInternalConfigSystem
	{
		private Configuration conf;

		public StubConfigSystem(Configuration conf)
		{
			this.conf = conf;
		}

		public object GetSection(string configKey)
		{
			var result = conf.GetSection(configKey) as object;
			if (configKey == "appSettings") {
				var appsettings = (AppSettingsSection)result;
				if (appsettings == null)
					return null;
				var nameValue = new NameValueCollection();
				result = nameValue;
				foreach (var key in appsettings.Settings.AllKeys) {
					nameValue.Add(key, appsettings.Settings[key].Value);
				}
			}
			return result;
		}

		public void RefreshConfig(string sectionName)
		{}

		public bool SupportsUserConfig { get; private set; }
	}

	public class Program
	{
				private static Dictionary<CultureInfo, Dictionary<string, string>> map =
			new Dictionary<CultureInfo, Dictionary<string, string>> {
					{ CultureInfo.GetCultureInfo("ru-RU"),
					new Dictionary<string, string> {
						{"default", "по умолчанию"},
						{"error-assembly-loading", "Ошибка при загрузке сборки '{0}'"},
						{"error-config-file-not-found", "Не удалось найти конфигурационный файл, пробовал найти по маскам '{0}'"},
						{"config-file-loading", "Загружаю конфигурационный файл '{0}'"},
						{"debug-assembly-resolved", "Поиск сборки '{0}', найдено '{1}' '{2}'"},

					}},
				{ CultureInfo.InvariantCulture, new Dictionary<string, string> {
						{"default", "by default"},
						{"error-asembly-loading", "Error on assembly '{0}' loading"},
						{"error-config-file-not-found", "Config file not found, search masks '{0}'"},
						{"config-file-loading", "Config file '{0}' loading"},
						{"debug-assembly-resolved", "Assembly '{0}' resolved '{1}' '{2}'"},
				}}
			};

		public static string i18n(string text)
		{
			return i18n(text, CultureInfo.CurrentCulture) ?? i18n(text, CultureInfo.InvariantCulture) ?? text;
		}

		private static string i18n(string text, CultureInfo culture)
		{
			if (map.ContainsKey(culture)) {
				var keys = map[culture];
				if (keys.ContainsKey(text))
					return keys[text];
			}
			return null;
		}

		public static void Main(string[] args)
		{
			try {
				var isDebug = false;
				var typeReg = new Regex(@".+\.Tasks\..+");
				var defaultBinPatterns = new List<string> { "src/*.Tasks/bin/debug/*.dll" };
				var binPatterns = new List<string>();
				var appConfig = new [] { "**/{0}/app.config", "**/{0}/web.config" };
				RymApp.DefaultMethod = "Execute";
				var ignoreMethods = new [] {"Dispose"};
				var procfile = "Rymfile";
				string workDir = null;
				var help = false;

				var rootOptions = new OptionSet {
					{"debug", v => isDebug = v != null},
					{"bin=", String.Format(i18n("default") + ": {0}", String.Join(", ",defaultBinPatterns)), v => binPatterns.Add(v)},
					{"config=", v => appConfig = new[] { v }},
					{"work-dir=", v => workDir = v},
					{"f|procfile=", String.Format(i18n("default") + ": {0}", procfile), v => procfile = v},
					{"h|help", v => help = v != null},
					{"typeReg", String.Format(i18n("default") + ": {0}", typeReg), v => typeReg = new Regex(v)},
				};
				var cliArgs = rootOptions.Parse(args);
				if (help) {
					rootOptions.WriteOptionDescriptions(Console.Out);
					return;
				}
				if (binPatterns.Count == 0)
					binPatterns = defaultBinPatterns;

				var regex = new Regex(@"^\s*#");
				if (File.Exists(procfile)) {
					rootOptions.Parse(File.ReadLines(procfile).Where(l => !regex.IsMatch(l)).Select(l => "--" + l));
				}

				var files = binPatterns.SelectMany(x => Glob.GetMatches(x, Glob.Constants.IgnoreCase)).ToList();

				//нужно хранить абсолютные пути на случай если
				//текущая директория будет модифицирована
				var assemblyLookup = files.SelectMany(f => Directory.GetFiles(Path.GetDirectoryName(f)))
					.GroupBy(f => Path.GetFileNameWithoutExtension(f))
					.ToLookup(f => f.Key, f => Path.GetFullPath(f.FirstOrDefault()),
						StringComparer.CurrentCultureIgnoreCase);

				AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) => {
					try {
						var filename = "";
						Assembly result = null;
						var name = new AssemblyName(eventArgs.Name);
						filename = assemblyLookup[name.Name].FirstOrDefault();
						if (!String.IsNullOrEmpty(filename)) {
							result = Assembly.LoadFrom(filename);
						}
						if (isDebug)
							Console.WriteLine(i18n("debug-assembly-resolved"),
								name,
								result,
								filename);
						return result;
					}
					catch(Exception e) {
						Console.Error.WriteLine(i18n("error-assembly-loading"), eventArgs.Name);
						Console.Error.WriteLine(e);
						throw;
					}
				};

				var config = appConfig.SelectMany(x => files.Select(y => String.Format(x, Path.GetFileNameWithoutExtension(y))))
					.SelectMany(c => Glob.GetMatches(c))
					.Concat(files
						.Select(x => new FileInfo(x + ".config"))
						.Where(x => x.Exists)
						.Select(x => x.FullName))
					.FirstOrDefault();

				if (config == null) {
					if (isDebug) {
						Console.WriteLine(i18n("error-config-file-not-found"),
							String.Join(", ", appConfig));
					}
				}
				else {
					if (isDebug) {
						Console.WriteLine(i18n("config-file-loading"), config);
					}
					var conf = ConfigurationManager.OpenMappedExeConfiguration(new ExeConfigurationFileMap {
						ExeConfigFilename = config
					}, ConfigurationUserLevel.None, true);
					typeof(ConfigurationManager)
						.GetMethod("SetConfigurationSystem", BindingFlags.Static | BindingFlags.NonPublic)
						.Invoke(null, new object[] {new StubConfigSystem(conf), true});
				}

				var types = files
					.Select(Assembly.LoadFrom)
					.SelectMany(a => a.GetTypes())
					.Where(t => t.IsClass && t.IsPublic && !t.IsAbstract
						&& typeReg.IsMatch(t.FullName))
					.Where(t => t.GetConstructor(new Type[0]) != null);

				var action = RymApp.GetAction(rootOptions, types, cliArgs, ignoreMethods);
				if (action == null)
					return;

				var origin = Environment.CurrentDirectory;
				try
				{
					if (!String.IsNullOrWhiteSpace(workDir))
						Environment.CurrentDirectory = workDir;
					action();
				}
				finally {
					Environment.CurrentDirectory = origin;
				}
			}
			catch(ReflectionTypeLoadException e) {
				foreach (var le in e.LoaderExceptions) {
					Console.Error.WriteLine(le);
				}
				throw;
			}
		}
	}
}
