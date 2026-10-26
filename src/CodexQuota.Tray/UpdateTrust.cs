using System.Security.Cryptography;

namespace CodexQuota.Tray;

internal static class UpdateTrust
{
    // Release preparation injects only the approved public key. Never embed a private key.
    private const string Modulus = "tOnb4h1V3nhQFpS368WqDRT+qHHWiSs+Y3/cWc6kv1G2/yyHnfm2DIy+usqM4vIp69/xZvSeUdcz1GKwY4Yxyy42XtJAk+PLgsq54VXXepQfgt6gjANJtOqrl/JTYN5maGrRIm1CGs2Pw3a3iY9tXQTAMwBtMr/Ce0YWWKw+W+SQmIOYJajFWMyQRv7Kq/P2G7a5OIh5YhCUT8dPL68Sp6WnFnbZoT/8vv9Lg8JQuwur0NnBr9xIEZ3oTSDanCqtaQcHxiVpxeaYv5r9qEbSooCRM0tUIoyPyKsivXgHJpqgdNLrrpfubIUalMQ28jCdhmiB7eLyUQSGzWBnZ95R769AoqoquQoTYZZOq8d3JY0Tj2bTZW6UQp/4PFJzFxL24mTlM0/lITI4oEqaV917aWag+NrsEc6+O8xMw4YoMb7MeIHT82HIj01qYMv7Hv/6Liq8vTn+AkNRyh5jxCKxQDLuWZyxFv+AkrnnafpgwlGAyFdPLp4Z95xV+vbAVKTD";
    private const string Exponent = "AQAB";
    public static bool IsConfigured => Modulus.Length >= 344;
    public static RSAParameters PublicKey => IsConfigured
        ? new RSAParameters { Modulus = Convert.FromBase64String(Modulus), Exponent = Convert.FromBase64String(Exponent) }
        : throw new InvalidOperationException("更新签名公钥尚未配置，不能安装更新。");
}
