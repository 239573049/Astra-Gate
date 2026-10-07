using Astra.Core.Clients;
using Microsoft.AspNetCore.DataProtection;

namespace Astra.Server.Security;

/// <summary>Encrypts provider API keys and local client keys at rest with ASP.NET DataProtection.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Astra.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
