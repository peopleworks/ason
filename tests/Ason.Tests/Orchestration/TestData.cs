using System.Collections.Generic;
using System.Linq;
using AsonRunner;

namespace Ason.Tests.Orchestration
{
    public class TestData
    {
        public static IEnumerable<object[]> GetE2ETestData()
        {
            yield return new object[]
            {
                "AddNumbers",
                """
                var simpleOp = testRootOp.GetSimpleOperator();
                TestModel model = new TestModel() { A = 2, B = 3 };
                return simpleOp.AddNumbers(model);
                """,
                "<task>\nsome task description\n</task>\n<result>\n5\n</result>",
                """
                script
                <task>
                some task description
                </task>
                """
            };
            yield return new object[]
            {
                "Concatenate",
                """
                var simpleOp = testRootOp.GetSimpleOperator();
                return simpleOp.Concatenate("hello", " world");
                """,
                "<task>\nsome task description\n</task>\n<result>\nhello world\n</result>",
                """
                script
                <task>
                some task description
                </task>
                """
            };
            yield return new object[]
            {
                "SumArray",
                """
                var simpleOp = testRootOp.GetSimpleOperator();
                return simpleOp.SumArray(new int[] { 1, 2, 3, 4 });
                """,
                "<task>\nsome task description\n</task>\n<result>\n10\n</result>",
                """
                script
                <task>
                some task description
                </task>
                """
            };
            yield return new object[]
            {
                "GetAnotherModel",
                """
                var simpleOp = testRootOp.GetSimpleOperator();
                var model = simpleOp.GetAnotherModel("test");
                return model.Name;
                """,
                "<task>\nsome task description\n</task>\n<result>\ntest\n</result>",
                """
                script
                <task>
                some task description
                </task>
                """
            };
            yield return new object[]
            {
                "ProcessAnotherModel",
                """
                var simpleOp = testRootOp.GetSimpleOperator();
                var model = new AnotherModel { Name = "test", Values = new List<int> { 1, 2, 3 } };
                return simpleOp.ProcessAnotherModel(model);
                """,
                "<task>\nsome task description\n</task>\n<result>\n3\n</result>",
                """
                script
                <task>
                some task description
                </task>
                """
            };
            yield return new object[]
            {
                "MultiplyAsync_AsSync",
                """
                var simpleOp = testRootOp.GetSimpleOperator();
                TestModel model = new TestModel() { A = 2, B = 3 };
                return simpleOp.Multiply(model);
                """,
                "<task>\nsome task description\n</task>\n<result>\n6\n</result>",
                """
                script
                <task>
                some task description
                </task>
                """
            };
            yield return new object[]
            {
                "ConcatAsync_AsSync",
                """
                var simpleOp = testRootOp.GetSimpleOperator();
                return simpleOp.Concat("foo", "bar");
                """,
                "<task>\nsome task description\n</task>\n<result>\nfoobar\n</result>",
                """
                script
                <task>
                some task description
                </task>
                """
            };
            yield return new object[]
            {
                "DoNothingAsync_AsSync",
                """
                var simpleOp = testRootOp.GetSimpleOperator();
                var model = new TestModel { A = 1, B = 2 };
                simpleOp.DoNothing(model);
                return "done";
                """,
                "<task>\nsome task description\n</task>\n<result>\ndone\n</result>",
                """
                script
                <task>
                some task description
                </task>
                """
            };
        }

        public static IEnumerable<object[]> RemoteExecutionTestData(params ExecutionMode[] executionModes)
        {
            var modes = (executionModes is { Length: > 0 })
                ? executionModes
                : new[] { ExecutionMode.ExternalProcess, ExecutionMode.Docker };

            foreach (var mode in modes)
            {
                foreach (var baseCase in GetE2ETestData())
                {
                    yield return new object[]
                    {
                        mode,
                        baseCase[0],
                        baseCase[1],
                        baseCase[2],
                        baseCase[3]
                    };
                }
            }
        }
    }
}
