using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NDesk.Options;
using NUnit.Framework;
using Rym;

namespace test
{
	[TestFixture]
	public class RymAppFixture
	{
		public class Test : RymApp
		{
			public void Execute()
			{

			}

			public void Action(int arg1, string args2)
			{
			}
		}
		public class Test2 : RymApp
		{
			public void Execute(string s)
			{

			}
		}

		[Test]
		public void Get_action()
		{
			RymApp.GetAction(new OptionSet(),
				new [] { typeof(Test) },
				new List<string> { "test", "action", "--arg1", "1", "arg2", "2" },
				new string[0]);
		}

		[Test]
		public void Exec_default_method()
		{
			RymApp.DefaultMethod = "Execute";
			var action = RymApp.GetAction(new OptionSet(),
				new [] { typeof(Test) },
				new List<string> { "test" },
				new string[0]);
			action();
		}

		[Test]
		public void Exec_default_method_with_params()
		{
			RymApp.DefaultMethod = "Execute";
			var action = RymApp.GetAction(new OptionSet(),
				new [] { typeof(Test2) },
				new List<string> { "test2", "123" },
				new string[0]);
			action();
		}

		[Test]
		public void Write_help()
		{
			var console = new StringWriter();
			try {
				Console.SetOut(console);
				RymApp.GetAction(new OptionSet(),
					new [] { typeof(Test) },
					new List<string> { "test1" },
					new string[0]);
				Assert.AreEqual("Не смог найти задачу test1, попробуй help что бы посмотреть все доступные задачи",
					console.ToString().Trim());
			} finally {
				Console.SetOut(Console.Out);
			}
		}
	}
}
