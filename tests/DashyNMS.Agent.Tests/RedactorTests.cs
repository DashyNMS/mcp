using DashyNMS.Agent.Redaction;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// One case that must be redacted and one that must be left alone per
/// pattern, on real-looking config lines with made-up secrets.
/// </summary>
public sealed class RedactorTests
{
    private const string R = Redactor.Marker;
    private readonly Redactor _redactor = new();

    [Theory]
    // SNMP communities: Cisco, AOS-S quoted
    [InlineData("snmp-server community Pub1icRW RW", $"snmp-server community {R} RW")]
    [InlineData("snmp-server community \"n0tPublic\" operator unrestricted", $"snmp-server community {R} operator unrestricted")]
    // SNMPv3: AOS-S and Cisco
    [InlineData("snmpv3 user \"monitor\" auth sha \"AuthPass123\" priv aes \"PrivPass456\"", $"snmpv3 user \"monitor\" auth sha {R} priv aes {R}")]
    [InlineData("snmp-server user mon grp v3 auth sha256 AuthPass123 priv aes 128 PrivPass456", $"snmp-server user mon grp v3 auth sha256 {R} priv aes 128 {R}")]
    // AOS-S local users
    [InlineData("password manager user-name \"admin\" sha1 \"5baa61e4c9b93f3f0682250b6cf8331b7ee68fd8\"", $"password manager user-name \"admin\" sha1 {R}")]
    // RADIUS and TACACS+
    [InlineData("radius-server host 10.0.0.5 key \"R4diusKey\"", $"radius-server host 10.0.0.5 key {R}")]
    [InlineData("tacacs-server key 7 0822455D0A16", $"tacacs-server key 7 {R}")]
    [InlineData("radius-server key S3cret", $"radius-server key {R}")]
    // Cisco passwords and secrets, with type numbers
    [InlineData("enable secret 5 $1$mERr$hx5rVt7rPNoS4wqbXKX7m0", $"enable secret 5 {R}")]
    [InlineData("enable password 7 0822455D0A16", $"enable password 7 {R}")]
    [InlineData("username admin privilege 15 secret 9 $9$abcDEF$xyz", $"username admin privilege 15 secret 9 {R}")]
    [InlineData(" password 7 121A0C041104", $" password 7 {R}")]
    // Keys
    [InlineData("crypto isakmp key 0 Sup3rPSK address 0.0.0.0", $"crypto isakmp key 0 {R} address 0.0.0.0")]
    [InlineData(" pre-shared-key ascii-text Sup3rPSK", $" pre-shared-key ascii-text {R}")]
    [InlineData(" key-string 7 104D000A0618", $" key-string 7 {R}")]
    [InlineData(" encrypted-key \"QWxhZGRpbjpvcGVuIHNlc2FtZQ==\"", $" encrypted-key {R}")]
    [InlineData("psk Sup3rPSK", $"psk {R}")]
    // Communities outside "snmp-server community": Cisco trap hosts, AOS-S trap hosts, Juniper
    [InlineData("snmp-server host 10.0.0.9 version 2c TrapC0mm", $"snmp-server host 10.0.0.9 version 2c {R}")]
    [InlineData("snmp-server host 10.0.0.9 informs version 2c TrapC0mm udp-port 162", $"snmp-server host 10.0.0.9 informs version 2c {R} udp-port 162")]
    [InlineData("snmp-server host 10.0.0.9 TrapC0mm", $"snmp-server host 10.0.0.9 {R}")]
    [InlineData("snmp-server host 10.0.0.5 community \"TrapC0mm\" trap-level all", $"snmp-server host 10.0.0.5 community {R} trap-level all")]
    [InlineData("set snmp community Jun1perC0mm authorization read-only", $"set snmp community {R} authorization read-only")]
    [InlineData("    community Jun1perC0mm {", $"    community {R} {{")]
    // Keys with an algorithm word before the value: NTP, OSPF, Cisco WLAN
    [InlineData("ntp authentication-key 1 md5 NtpK3y 7", $"ntp authentication-key 1 md5 {R} 7")]
    [InlineData(" ip ospf message-digest-key 1 md5 0spfK3y", $" ip ospf message-digest-key 1 md5 {R}")]
    [InlineData(" wpa-psk ascii 0 WlanK3y", $" wpa-psk ascii 0 {R}")]
    // Linux and ini-style assignments
    [InlineData("wpa_passphrase=CorrectHorse", $"wpa_passphrase={R}")]
    [InlineData("password = hunter2", $"password = {R}")]
    [InlineData("api_key: abc123def", $"api_key: {R}")]
    [InlineData("\"password\": \"hunter2\"", $"\"password\": {R}")]
    public void Redacts_secret_values_and_keeps_the_keyword(string input, string expected)
        => Assert.Equal(expected, _redactor.Redact(input));

    [Theory]
    [InlineData("snmp-server contact \"Network Operations\"")]
    [InlineData("snmp-server location \"Rack 4\"")]
    [InlineData("interface 1/1/1")]
    [InlineData("password-policy minimum-length 12")]
    [InlineData("no aaa authentication login privilege-mode")]
    [InlineData("crypto key generate rsa modulus 2048")]
    [InlineData("ip ssh key-exchange curve25519-sha256")]
    [InlineData("radius-server host 10.0.0.5 auth-port 1812")]
    [InlineData("Port 1/1/7 link down")]
    [InlineData("hostname core-sw1")]
    [InlineData(" neighbor 10.0.0.2 send-community both")]
    [InlineData("ip community-list standard CUSTOMERS permit 65000:100")]
    [InlineData("snmp-server host 10.0.0.9 version 3 priv monitor")]
    [InlineData("<descr>LAN interface</descr>")]
    [InlineData("-----BEGIN CERTIFICATE-----\nMIIBszCCAVmg\n-----END CERTIFICATE-----")]
    public void Leaves_ordinary_text_alone(string input)
        => Assert.Equal(input, _redactor.Redact(input));

    [Fact]
    public void Redacts_private_key_blocks_whole()
    {
        var input = "before\n-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA0Z3V\nS1Ms7g==\n-----END RSA PRIVATE KEY-----\nafter";

        var result = _redactor.Redact(input);

        Assert.Equal($"before\n-----BEGIN RSA PRIVATE KEY-----{R}-----END RSA PRIVATE KEY-----\nafter", result);
    }

    [Fact]
    public void Redacts_openssh_and_pkcs8_keys()
    {
        Assert.DoesNotContain("b3BlbnNzaC", _redactor.Redact("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----"), StringComparison.Ordinal);
        Assert.DoesNotContain("MIIEvQ", _redactor.Redact("-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBg\n-----END PRIVATE KEY-----"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<password>hunter2</password>", $"<password>{R}</password>")]
    [InlineData("<bcrypt-hash>$2y$10$abcdefghijkl</bcrypt-hash>", $"<bcrypt-hash>{R}</bcrypt-hash>")]
    [InlineData("<pre-shared-key>Sup3rPSK</pre-shared-key>", $"<pre-shared-key>{R}</pre-shared-key>")]
    [InlineData("<apikey>0123456789abcdef</apikey>", $"<apikey>{R}</apikey>")]
    [InlineData("<prv>LS0tLS1CRUdJTiBQUklWQVRF</prv>", $"<prv>{R}</prv>")]
    public void Redacts_pfsense_secret_elements(string input, string expected)
        => Assert.Equal(expected, _redactor.Redact(input));

    /// <summary>
    /// Gaps found by checking real backups by hand (#14): the shapes are the
    /// vendors' own, the secrets made up.
    /// </summary>
    [Theory]
    // FortiOS: the ENC marker isn't the secret
    [InlineData("    set password ENC SH2FakeHashForTestsOnly=", $"    set password ENC {R}")]
    [InlineData("    set passwd ENC GjaYrYzVxn0u3xR6gAbCdEf==", $"    set passwd ENC {R}")]
    [InlineData("        set psksecret ENC ZHVtbXlQU0tkdW1teQ==", $"        set psksecret ENC {R}")]
    // AOS-CX: ciphertext and plaintext are qualifiers
    [InlineData("user admin group administrators password ciphertext AQBapVcYpG2k1nQ0dW1teQ==", $"user admin group administrators password ciphertext {R}")]
    [InlineData("radius-server host 10.0.0.5 key ciphertext AQBapZ2R1bW15S2V5", $"radius-server host 10.0.0.5 key ciphertext {R}")]
    [InlineData("snmpv3 user mon auth sha auth-pass ciphertext AQBaAuth priv aes priv-pass ciphertext AQBaPriv", $"snmpv3 user mon auth sha auth-pass ciphertext {R} priv aes priv-pass ciphertext {R}")]
    // Planet: password encrypted <hash>
    [InlineData("username admin privilege 15 password encrypted 2b4e7c1f0a9d3e8b", $"username admin privilege 15 password encrypted {R}")]
    // Brocade FOS configupload: camel-case keys
    [InlineData("snmp.snmpv3Usm.0.usmAuthSecret:AbCdEf123456==", $"snmp.snmpv3Usm.0.usmAuthSecret:{R}")]
    [InlineData("snmp.snmpv3Usm.3.usmPrivSecret:QrStUv789==", $"snmp.snmpv3Usm.3.usmPrivSecret:{R}")]
    [InlineData("snmp.agtParty.0.authPrivSecret:XyZ0987", $"snmp.agtParty.0.authPrivSecret:{R}")]
    [InlineData("snmp.snmpv3Usm.1.usmAuthSecret.default:AbCdEf123456==", $"snmp.snmpv3Usm.1.usmAuthSecret.default:{R}")]
    // Teltonika RutOS / OpenWrt UCI
    [InlineData("\toption key 'WifiK3y123'", $"\toption key {R}")]
    [InlineData("\toption community 'C0mmunity'", $"\toption community {R}")]
    [InlineData("\toption password 'P4ssw0rd!'", $"\toption password {R}")]
    [InlineData("\toption 'password' 'Qu0tedNam3!'", $"\toption 'password' {R}")]
    [InlineData("pass=Secr3t12", $"pass={R}")]
    // Crypt hashes wherever they are: /etc/shadow lines, scripts
    [InlineData("root:$1$abcd1234$Xyz0123456789abcdefgh:19000:0:99999:7:::", $"root:{R}:19000:0:99999:7:::")]
    [InlineData("admin:$6$saltsalt$0123456789abcdefABCDEF./0123456789:19500::::::", $"admin:{R}:19500::::::")]
    public void Redacts_secrets_found_in_real_backups(string input, string expected)
        => Assert.Equal(expected, _redactor.Redact(input));

    [Theory]
    [InlineData("    set type password")]
    [InlineData("    set type password\n    set comments \"guest account\"")]
    [InlineData("trace.ftp.password:\nsnmp.portFilterBlockMask:0")]
    [InlineData("passwdcfg.hash:sha512")]
    [InlineData("password_policy:strong")]
    [InlineData("\toption key_size '2048'")]
    [InlineData("\toption authorization 'password'")]
    [InlineData("hostkeyalg.ssh:ssh-ed25519")]
    [InlineData("no service password-encryption")]
    public void Leaves_lookalikes_from_real_backups_alone(string input)
        => Assert.Equal(input, _redactor.Redact(input));

    [Theory]
    [InlineData("<rocommunity>C0mmunity</rocommunity>", $"<rocommunity>{R}</rocommunity>")]
    [InlineData("<trapstring>TrapC0mm</trapstring>", $"<trapstring>{R}</trapstring>")]
    [InlineData("<tls>IyBPcGVuVlBOIFN0YXRpYyBrZXk=</tls>", $"<tls>{R}</tls>")]
    [InlineData("<shared_key>IyBPcGVuVlBOIFNoYXJlZA==</shared_key>", $"<shared_key>{R}</shared_key>")]
    [InlineData("<pwhash>$2y$10$abcdefghijklmnopqrstuv</pwhash>", $"<pwhash>{R}</pwhash>")]
    [InlineData("<device_key>q83vEjRWeJA=</device_key>", $"<device_key>{R}</device_key>")]
    [InlineData("<ldap_bindpw>B1ndPw</ldap_bindpw>", $"<ldap_bindpw>{R}</ldap_bindpw>")]
    [InlineData("<radius_secret>R4dius</radius_secret>", $"<radius_secret>{R}</radius_secret>")]
    [InlineData("<privatekey>WGprivKEYdummy=</privatekey>", $"<privatekey>{R}</privatekey>")]
    [InlineData("<presharedkey>WGpskDUMMY=</presharedkey>", $"<presharedkey>{R}</presharedkey>")]
    public void Redacts_more_pfsense_secret_elements(string input, string expected)
        => Assert.Equal(expected, _redactor.Redact(input));

    [Fact]
    public void Redacts_a_base64_element_whose_decoded_text_holds_a_secret()
    {
        // pfSense keeps the FRR config base64-encoded, and a model reads base64 as easily as text.
        var frr = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("router bgp 65000\n neighbor 10.0.0.2 remote-as 65001\n neighbor 10.0.0.2 password BgpS3cret\n"));

        Assert.Equal($"<frrrunning>{R}</frrrunning>", _redactor.Redact($"<frrrunning>{frr}</frrrunning>"));
    }

    [Fact]
    public void Leaves_base64_elements_without_secrets_alone()
    {
        var banner = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Authorised users only. All activity is monitored and logged."));
        // DER starts with 0x30 0x82 and isn't text, like any certificate.
        var certificate = "<crt>" + Convert.ToBase64String([0x30, 0x82, 0x03, 0x77, 0x30, 0x82, 0x02, 0x5F, 0xA0, 0x03, 0x02, 0x01, 0x02, 0x02, 0x04, 0x02, 0x00, 0x00, 0xB9, 0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x05, 0x05, 0x00, 0x30, 0x5A, 0x31, 0x0B, 0x30, 0x09]) + "</crt>";

        Assert.Equal($"<login_message>{banner}</login_message>", _redactor.Redact($"<login_message>{banner}</login_message>"));
        Assert.Equal(certificate, _redactor.Redact(certificate));
    }

    [Fact]
    public void Redacts_every_secret_in_a_config_diff()
    {
        var diff = """
            @@ -12,7 +12,7 @@
             hostname "edge-sw3"
            -snmp-server community "OldC0mmunity" operator
            +snmp-server community "NewC0mmunity" operator
             radius-server host 10.0.0.5 key "R4diusKey"
            -password manager user-name "admin" sha1 "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            +password manager user-name "admin" sha1 "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            """;

        var result = _redactor.Redact(diff);

        foreach (var secret in new[] { "OldC0mmunity", "NewC0mmunity", "R4diusKey", "aaaaaaaa", "bbbbbbbb" })
        {
            Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        }

        Assert.Contains("hostname \"edge-sw3\"", result, StringComparison.Ordinal);
        Assert.Contains($"-snmp-server community {R} operator", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacting_twice_changes_nothing_more()
    {
        var once = _redactor.Redact(
            "enable secret 5 abc\npassword=xyz\nsnmp-server community pub RO\ntacacs-server key 7 0822\nsnmp-server user u g v3 auth sha a priv aes 128 b\n" +
            "snmp-server host 10.0.0.9 version 2c c\nntp authentication-key 1 md5 k 7\n wpa-psk ascii 0 w\nset snmp community j authorization read-only");

        Assert.Equal(once, _redactor.Redact(once));
    }
}
