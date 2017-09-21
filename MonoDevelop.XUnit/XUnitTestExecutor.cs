//
// XUnitTestExecutor.cs
//
// Author:
//       Sergey Khabibullin <sergey@khabibullin.com>
//
// Copyright (c) 2014 Sergey Khabibullin
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System.Collections.Generic;
using MonoDevelop.UnitTesting.XUnit;
using MonoDevelop.UnitTesting;
using MonoDevelop.UnitTesting.XUnit.External;
using System.IO;
using System;
using MonoDevelop.Core;

namespace MonoDevelop.XUnit
{
	/// <summary>
	/// Wrapper around XUnitTestRunner. It extracts all information needed to
	/// run tests, then dispatches rusults back to tests.
	/// </summary>
	internal class XUnitTestExecutor
	{
		/// <summary>
		/// Runs a single test case.
		/// </summary>
		/// <returns>The test case.</returns>
		/// <param name="rootSuite">Root suite.</param>
		/// <param name="testCase">Test case.</param>
		/// <param name="context">Context.</param>
		public UnitTestResult RunTestCase (XUnitAssemblyTestSuite rootSuite, XUnitTestCase testCase, TestContext context)
		{
			return Run (new List<XUnitTestCase> { testCase }, rootSuite, testCase, context, false);
		}

		/// <summary>
		/// Runs the entire test suite.
		/// </summary>
		/// <returns>The test suite.</returns>
		/// <param name="rootSuite">Root suite.</param>
		/// <param name="testSuite">Test suite.</param>
		/// <param name="context">Context.</param>
		public UnitTestResult RunTestSuite (XUnitAssemblyTestSuite rootSuite, XUnitTestSuite testSuite, TestContext context)
		{
			var testCases = new List<XUnitTestCase> ();
			CollectTestCases (testCases, testSuite);

			return Run (testCases, rootSuite, testSuite, context);
		}

		/// <summary>
		/// Runs all test suites in the assembly.
		/// </summary>
		/// <returns>The assembly test suite.</returns>
		/// <param name="assemblyTestSuite">Assembly test suite.</param>
		/// <param name="context">Context.</param>
		public UnitTestResult RunAssemblyTestSuite (XUnitAssemblyTestSuite assemblyTestSuite, TestContext context)
		{
			var testCases = new List<XUnitTestCase> ();

			foreach (var test in assemblyTestSuite.Tests) {
				var testSuite = test as XUnitTestSuite;
				if (testSuite != null)
					CollectTestCases (testCases, testSuite);
			}

			return Run (testCases, assemblyTestSuite, assemblyTestSuite, context);
		}

		void CollectTestCases (List<XUnitTestCase> testCases, XUnitTestSuite testSuite)
		{
			foreach (var test in testSuite.Tests) {
				if (test is XUnitTestCase)
					testCases.Add ((XUnitTestCase)test);
				else
					CollectTestCases (testCases, (XUnitTestSuite)test);
			}
		}

		/// <summary>
		/// Run the specified testCases, rootSuite, test, context and reportToMonitor.
		/// </summary>
		/// <param name="testCases">Test cases.</param>
		/// <param name="rootSuite">Root suite.</param>
		/// <param name="test">Test.</param>
		/// <param name="context">Context.</param>
		/// <param name="reportToMonitor">Report to monitor.</param>
		/// <remarks>
		/// This is actual the code that executes the test cases.
		/// 
		/// It uses the MonoDevelop built-in .NET remoting helper to execute the code of <seealso cref="XUnitTestRunner"/> in a separate process.
		/// 
		/// If any debugging is required, simply comment out the remoting part, and call <seealso cref="XUnitTestRunner"/> directly,
		/// so that the code executes inside MonoDevelop.
		/// </remarks>
		UnitTestResult Run(List<XUnitTestCase> testCases, XUnitAssemblyTestSuite rootSuite, IExecutableTest test, TestContext context, bool reportToMonitor = true)
		{
#if EASY_DEBUGGING
			using (var session = test.CreateExecutionSession(reportToMonitor)) {
				var runner = new XUnitRunner.XUnitRunner();
				var localTestMonitor = new LocalTestMonitor(context, rootSuite, rootSuite.Name, false);

				string[] nameFilter = new string[testCases.Count];
				for (var i = 0; i < testCases.Count; ++i) {
					nameFilter[i] = testCases[i].TestInfo.Id;
				}

				var path = rootSuite.AssemblyPath;
				runner.Execute(path, nameFilter, localTestMonitor);
				return session.Result;
			}
#else
			using (var session = test.CreateExecutionSession(reportToMonitor)) {
				using (var runner = new ExternalTestRunner(rootSuite.Project)) {
					runner.Connect(XUnitVersion.XUnit2, context.ExecutionContext.ExecutionHandler).Wait();
					var localTestMonitor = new LocalTestMonitor(context, rootSuite, rootSuite.Name, false);

					string[] nameFilter = new string[testCases.Count];
					for (var i = 0; i < testCases.Count; ++i) {
						nameFilter[i] = testCases[i].TestInfo.Id;
					}

					var path = rootSuite.AssemblyPath;
					var supportAssemblies = new List<string>();

					RunData rd = new RunData();
					rd.Runner = runner;
					rd.LocalMonitor = localTestMonitor;

					var cancelReg = context.Monitor.CancellationToken.Register(rd.Cancel);

					UnitTestResult result;
					var crashLogFile = Path.GetTempFileName();

					try {

						context.Monitor.CancellationToken.ThrowIfCancellationRequested();

						runner.Run(localTestMonitor, nameFilter, path, "", supportAssemblies, null, null, crashLogFile).Wait();

					} catch (Exception ex) {
						if (!localTestMonitor.Canceled) {
							LoggingService.LogError(ex.ToString());
							result = UnitTestResult.CreateFailure(ex);
						} else {
							result = UnitTestResult.CreateFailure(GettextCatalog.GetString("Canceled"), null);
						}
					} finally {
						// Dispose the runner before the console, to make sure the console is available until the runner is disposed.
						runner.Dispose();
						cancelReg.Dispose();
						File.Delete(crashLogFile);
					}
				
				}
				return session.Result;
			}
#endif
		}

		class RunData
		{
			public ExternalTestRunner Runner;
			public UnitTest Test;
			public LocalTestMonitor LocalMonitor;

			public void Cancel()
			{
				LocalMonitor.Canceled = true;
				Runner.Dispose();
				ClearRunningStatus(Test);
			}

			void ClearRunningStatus(UnitTest t)
			{
				t.Status = TestStatus.Ready;
				UnitTestGroup group = t as UnitTestGroup;
				if (group == null) return;
				foreach (UnitTest ct in group.Tests)
					ClearRunningStatus(ct);
			}
		}
	}
}
