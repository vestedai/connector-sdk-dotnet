using System.Reflection;
using VestedAI.ConnectorSdk;

return await ConnectorHost
    .CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    .Build()
    .RunFromEnvironmentAsync();
