using Ason;
using Ason.CodeGen;
using ConsoleExtractorSample;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

string userMessage = "Use the information from the email to get John’s and Bob’s apples, and then calculate the total number of apples";

var operatorLibrary = new OperatorBuilder()
    .AddAssemblies(typeof(MyOperator).Assembly)
    .AddExtractor()
    .Build();

var apiKey = Environment.GetEnvironmentVariable("MY_OPEN_AI_KEY") ?? string.Empty;
IChatCompletionService chatService = new OpenAIChatCompletionService(modelId: "gpt-4.1-mini", apiKey: apiKey);

var myOperator = new MyOperator(new object());
AsonClient client = new AsonClient(chatService, myOperator, operatorLibrary);

Console.WriteLine($"Methods agent can use: MyOperator.Add, MyOperator.GetEmailText, ExtractionOperator.ExtractDataFromText");
Console.WriteLine($"Sample information in your email database: {myOperator.GetEmailText()}");
Console.WriteLine($"User: {userMessage}");

var result = await client.SendAsync(userMessage);

Console.WriteLine($"Agent: {result}");


Console.WriteLine("Press any key to exit...");
Console.ReadKey(intercept: true);

