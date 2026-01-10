
using Ason;

namespace ConsoleExtractorSample;

public class MyOperator : RootOperator {
    public MyOperator(object attachedObject) : base(attachedObject) {
    }

    [AsonMethod]
    public int Add(int A, int B) => A + B;

    [AsonMethod]
    public string GetEmailText() => "Hey! John has 7 apples, and Bob has only three.";
}
