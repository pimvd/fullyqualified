using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FullyQualified.Web;

/// <summary>In-memory certificate inspection. Chain construction never downloads issuer certificates.</summary>
public static class CertificateTools
{
    public static ToolResult Inspect(ToolRequest request)
    {
        byte[] data;
        if (!string.IsNullOrWhiteSpace(request.Text))
        {
            if (Encoding.UTF8.GetByteCount(request.Text) > 64 * 1024) throw new ToolException("too_large");
            data = Encoding.UTF8.GetBytes(request.Text);
        }
        else
        {
            if (request.FileBase64?.Length > 350_000) throw new ToolException("too_large");
            try { data = Convert.FromBase64String(request.FileBase64 ?? ""); }
            catch (FormatException) { throw new ToolException("invalid_certificate"); }
        }
        if (data.Length > 256 * 1024) throw new ToolException("too_large");
        var text = Encoding.UTF8.GetString(data);
        if (text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)) throw new ToolException("private_key");
        var certificates = new List<X509Certificate2>();
        try
        {
            if (text.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                var remaining = text.AsSpan();
                while (PemEncoding.TryFind(remaining, out var fields))
                {
                    if (!remaining[..fields.Location.Start].Trim().IsEmpty) throw new ToolException("invalid_certificate");
                    if (!remaining[fields.Label].SequenceEqual("CERTIFICATE")) throw new ToolException("invalid_certificate");
                    certificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(remaining[fields.Base64Data].ToString())));
                    remaining = remaining[fields.Location.End..];
                    if (certificates.Count > 32) throw new ToolException("too_large");
                }
                if (!remaining.Trim().IsEmpty) throw new ToolException("invalid_certificate");
            }
            else certificates.Add(X509CertificateLoader.LoadCertificate(data));
            if (certificates.Count == 0) throw new ToolException("invalid_certificate");
            var sections = new List<ToolSection> { new("Tool_CertificateInspection", [new("Tool_CertificateScope", "Tool_CertificateLocalOnly")]) };
            foreach (var certificate in certificates) sections.Add(Describe(certificate));
            sections.Add(BuildChain(certificates[0], certificates.Skip(1)));
            return new(sections);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { throw new ToolException("invalid_certificate"); }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    internal static ToolSection Describe(X509Certificate2 cert)
    {
        var now = DateTime.UtcNow;
        var rows = new List<ToolRow>
        {
            new("Tool_CertificateSubject", cert.Subject), new("Tool_CertificateIssuer", cert.Issuer),
            new("Tool_CertificateNotBefore", cert.NotBefore.ToUniversalTime().ToString("O")),
            new("Tool_CertificateNotAfter", cert.NotAfter.ToUniversalTime().ToString("O")),
            new("Tool_CertificateFingerprint", cert.GetCertHashString(HashAlgorithmName.SHA256)),
            new("Tool_CertificateSignature", cert.SignatureAlgorithm.Value ?? ""),
            new("Tool_CertificateExpired", now > cert.NotAfter.ToUniversalTime() ? "Tool_Yes" : "Tool_No", now > cert.NotAfter.ToUniversalTime() ? "warning" : "info"),
            new("Tool_CertificateNotYetValid", now < cert.NotBefore.ToUniversalTime() ? "Tool_Yes" : "Tool_No", now < cert.NotBefore.ToUniversalTime() ? "warning" : "info")
        };
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is not null)
        {
            rows.Add(new("Tool_CertificateDnsNames", string.Join(", ", san.EnumerateDnsNames())));
            rows.Add(new("Tool_CertificateIpNames", string.Join(", ", san.EnumerateIPAddresses())));
        }
        using var rsa = cert.GetRSAPublicKey(); using var ec = cert.GetECDsaPublicKey(); using var dsa = cert.GetDSAPublicKey();
        rows.Add(new("Tool_CertificatePublicKey", $"{cert.PublicKey.Oid.Value}; {rsa?.KeySize ?? ec?.KeySize ?? dsa?.KeySize ?? 0}"));
        return new("Tool_CertificateDetails", rows);
    }

    internal static ToolSection BuildChain(X509Certificate2 leaf, IEnumerable<X509Certificate2> intermediates)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        foreach (var intermediate in intermediates) chain.ChainPolicy.ExtraStore.Add(intermediate);
        var valid = chain.Build(leaf);
        var rows = new List<ToolRow> { new("Tool_CertificateChainValidation", valid ? "Tool_Accepted" : "Tool_CertificateChainErrors", valid ? "info" : "warning", "Tool_CertificateChainNoRevocation") };
        foreach (var element in chain.ChainElements)
            rows.Add(new("Tool_CertificateChainElement", element.Certificate.Subject));
        foreach (var status in chain.ChainStatus)
            rows.Add(new("Tool_CertificateChainStatus", status.Status.ToString(), "warning"));
        return new("Tool_CertificateLocalChain", rows);
    }
}
