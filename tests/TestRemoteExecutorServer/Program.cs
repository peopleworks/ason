using AsonRemoteRunner;

namespace TestRemoteExecutorServer; 
public class Program {
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAsonScriptRunner();
        var app = builder.Build();

        //if (app.Environment.IsDevelopment()) {
        //    app.MapOpenApi();
        //}

        //app.UseHttpsRedirection();
        //app.MapGet("/ping", () => "pong");
        app.MapAson("/scriptRunnerHub", requireAuthorization: false);


        app.Run();
    }
}