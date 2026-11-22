#:property PublishAot=false
#:property NuGetAudit=false

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

if (Console.ReadLine() is not { } encoded)
    return 1;

using var leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(encoded));
var extension = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
foreach (Oid usage in extension.EnhancedKeyUsages)
{
    var hash = SHA256.HashData(Encoding.ASCII.GetBytes(usage.Value!));
    Console.WriteLine($"EKU {Convert.ToHexStringLower(hash)}");
}
return 0;
