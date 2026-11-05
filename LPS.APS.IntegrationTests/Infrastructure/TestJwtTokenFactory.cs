using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace LPS.APS.IntegrationTests.Infrastructure;

/// <summary>
/// 用 Web 侧**同一套** Jwt 参数（密钥/签发者/受众）自签测试令牌。
/// <para>
/// 读的是 <c>LPS.APS.Web/appsettings.json</c>（＋ Development 覆盖），与宿主实际校验的配置同源，
/// 不硬编码密钥——否则密钥一改测试就会以「403」的形式静默失真。
/// </para>
/// </summary>
public sealed class TestJwtTokenFactory
{
    private readonly string _secretKey;
    private readonly string _issuer;
    private readonly string _audience;

    public TestJwtTokenFactory(string webProjectRoot)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(webProjectRoot)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        _secretKey = configuration["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("appsettings 缺少 Jwt:SecretKey");
        _issuer = configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException("appsettings 缺少 Jwt:Issuer");
        _audience = configuration["Jwt:Audience"]
            ?? throw new InvalidOperationException("appsettings 缺少 Jwt:Audience");
    }

    /// <summary>签一个「已认证」令牌（不含任何权限码——权限由 <c>IPermissionService</c> 替身决定）。</summary>
    public string CreateAuthenticatedToken(int userId = ApsWebAppFactory.TestUserId, string userCode = ApsWebAppFactory.TestUserCode)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, userCode)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
