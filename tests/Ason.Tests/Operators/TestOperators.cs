using Ason.Tests.Orchestration;

namespace Ason.Tests.Operators;

[AsonOperator]
public class TestRootOp : RootOperator {
    public TestRootOp(object obj) : base(obj) { }

    [AsonMethod]
    public async Task<SimpleOp> GetSimpleOperatorAsync() =>
        await GetViewOperator<SimpleOp>(() => {
            StubView view = new StubView(this);
            view.CompleteInitialization();
        });
}



[AsonOperator]
public class SimpleOp : OperatorBase {
    public SimpleOp() { }

    [AsonMethod]
    public int AddNumbers(TestModel model) => model.A + model.B;

    [AsonMethod]
    public async Task<int> AddAsync(int a, int b) { await Task.Yield(); return a + b; }

    [AsonMethod]
    public async Task<int> MultiplyAsync(TestModel model) { await Task.Yield(); return model.A * model.B; }

    [AsonMethod]
    public async Task<string> ConcatAsync(string a, string b) { await Task.Yield(); return a + b; }

    [AsonMethod]
    public async Task DoNothingAsync(TestModel model) { await Task.Yield(); }

    [AsonMethod]
    public string Concatenate(string a, string b) => a + b;

    [AsonMethod]
    public TestModel GetDefaultModel() => new TestModel { A = 1, B = 2 };

    [AsonMethod]
    public int SumArray(int[] numbers) => numbers.Sum();

    [AsonMethod]
    public async Task<string> LongRunningOperation(string input)
    {
        await Task.Delay(100);
        return $"Completed: {input}";
    }

    [AsonMethod]
    public void NoReturnValue() { }

    [AsonMethod]
    public static string StaticMethod() => "static";

    [AsonMethod]
    public AnotherModel GetAnotherModel(string name) => new AnotherModel { Name = name, Values = new List<int> { 1, 2, 3 } };

    [AsonMethod]
    public int ProcessAnotherModel(AnotherModel model) => model.Values.Count;
}

[AsonModel(McpToolName = "TestMcpServer.Add")]
public class TestModel {
    public int A { get; set; }
    public int B { get; set; }
}

[AsonModel]
public class AnotherModel
{
    public string Name { get; set; }
    public List<int> Values { get; set; }
}
