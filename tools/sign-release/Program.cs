// Release-Signatur (Audit S2): signiert SHA256SUMS.txt mit dem privaten Schlüssel aus der Umgebungsvariable
// RELEASE_SIGNING_KEY (PKCS#8, Base64) und prüft die Signatur sofort mit den in der App eingebauten öffentlichen
// Schlüsseln. Passt das Secret nicht zum Code, endet das Programm mit Fehler – der Release wird dann nicht veröffentlicht.
//
// Aufruf: dotnet run --project tools/sign-release -- <SHA256SUMS.txt> <SHA256SUMS.txt.sig>
using BitaxeTuner.Core.Update;

if (args.Length != 2)
{
    Console.Error.WriteLine("Aufruf: sign-release <SHA256SUMS.txt> <SHA256SUMS.txt.sig>");
    return 2;
}
var key = Environment.GetEnvironmentVariable("RELEASE_SIGNING_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("RELEASE_SIGNING_KEY fehlt – Release wird nicht unsigniert veröffentlicht.");
    return 1;
}
var sums = File.ReadAllBytes(args[0]);
var signature = ReleaseSignature.Sign(sums, key);
if (!ReleaseSignature.Verify(sums, signature))
{
    Console.Error.WriteLine("Signatur passt zu keinem eingebauten Schlüssel (ReleaseSignature.TrustedKeys) – Release abgebrochen.");
    return 1;
}
File.WriteAllText(args[1], signature);
Console.WriteLine($"{Path.GetFileName(args[0])} signiert und gegen {ReleaseSignature.TrustedKeys.Count} eingebaute Schlüssel geprüft.");
return 0;
