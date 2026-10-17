using BitaxeTuner.Core.Transfer;

namespace BitaxeTuner.Tests;

/// <summary>0.9.22: SSH nach Pi-Tausch – Ausgabe von OpenSSH richtig deuten.</summary>
public class SshDiagnosisTests
{
    [Fact]
    public void Host_key_change_rejected_key_and_unreachable_are_told_apart()
    {
        Assert.Equal(SshProbeResult.Ok, SshDiagnosis.Classify("BT-SSH-OK\n", 0));
        Assert.Equal(SshProbeResult.HostKeyChanged, SshDiagnosis.Classify("""
            @@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@
            @    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @
            The fingerprint for the ED25519 key sent by the remote host is
            SHA256:TestFingerabdruck0000000000000000000000AAA.
            Host key verification failed.
            """, 255));
        Assert.Equal(SshProbeResult.KeyRejected, SshDiagnosis.Classify("pi@10.0.15.1: Permission denied (publickey).", 255));
        Assert.Equal(SshProbeResult.Unreachable, SshDiagnosis.Classify("ssh: connect to host 10.0.15.1 port 22: Connection timed out", 255));
        Assert.Equal(SshProbeResult.Other, SshDiagnosis.Classify("irgendwas", 1));
        Assert.NotEqual(SshProbeResult.Ok, SshDiagnosis.Classify("BT-SSH-OK", 1));   // Exit-Code zählt
    }

    [Fact]
    public void Fingerprints_and_keyscan_lines_are_read()
    {
        Assert.Equal("SHA256:TestFingerabdruck0000000000000000000000AAA",
            SshDiagnosis.Fingerprint("256 SHA256:TestFingerabdruck0000000000000000000000AAA 10.0.15.1 (ED25519)"));
        Assert.Null(SshDiagnosis.Fingerprint("# Host 10.0.15.1 not found"));
        var lines = SshDiagnosis.KnownHostLines("""
            # 10.0.15.1:22 SSH-2.0-OpenSSH_9.2p1 Debian
            10.0.15.1 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleKeyMaterial
            anderer ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFremd
            """, "10.0.15.1");
        Assert.Equal(["10.0.15.1 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleKeyMaterial"], lines);   // nur passender Host, keine Kommentare
    }
}
