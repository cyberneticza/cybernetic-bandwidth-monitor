namespace CyberneticBandwidth.App.Services;

/// <summary>
/// Plain names for the ports people actually meet on a Windows PC. A port only says what a
/// connection is probably for; anything can run on any port, so this is a hint, not a verdict.
/// </summary>
public static class WellKnownPorts
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [20] = "FTP data",
        [21] = "FTP",
        [22] = "SSH",
        [25] = "Email (SMTP)",
        [53] = "DNS lookup",
        [67] = "DHCP",
        [68] = "DHCP",
        [80] = "Web (HTTP)",
        [88] = "Kerberos sign-in",
        [110] = "Email (POP3)",
        [123] = "Time sync",
        [135] = "Windows RPC",
        [137] = "NetBIOS",
        [138] = "NetBIOS",
        [139] = "File sharing (NetBIOS)",
        [143] = "Email (IMAP)",
        [389] = "Directory (LDAP)",
        [443] = "Web (HTTPS)",
        [445] = "File sharing (SMB)",
        [465] = "Email (SMTPS)",
        [587] = "Email (submission)",
        [636] = "Directory (LDAPS)",
        [853] = "Encrypted DNS",
        [993] = "Email (IMAPS)",
        [995] = "Email (POP3S)",
        [1900] = "Device discovery (SSDP)",
        [3389] = "Remote Desktop",
        [3478] = "Calls (STUN)",
        [3479] = "Calls (STUN)",
        [3480] = "Calls (STUN)",
        [3481] = "Calls (STUN)",
        [5353] = "Local discovery (mDNS)",
        [5355] = "Local name lookup (LLMNR)",
        [7680] = "Windows Update sharing",
        [8080] = "Web (alternate)",
        [8443] = "Web (alternate HTTPS)"
    };

    public static string Describe(int port) => port switch
    {
        _ when Names.TryGetValue(port, out var name) => name,
        >= 49152 => "Temporary port",
        _ => "—"
    };
}
