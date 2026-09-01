using System.Reflection;

namespace Workbench.Server.Application;

internal sealed class AssemblyReleaseInformation : IReleaseInformation
{
    public string Version => typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "0.0.0-local";
}
