using System.Reflection;
using System.Text;

namespace CodexQuota.Tray;

internal sealed class LicenseDialog : Form
{
    public LicenseDialog()
    {
        Text = "Codex 额度 · 关于与第三方许可";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(740, 540);
        MinimumSize = new Size(480, 320);
        var content = new StringBuilder();
        content.AppendLine("CodexQuotaTray " + Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) + " · 个人辅助工具，非 OpenAI 官方产品");
#if NETFRAMEWORK
        content.AppendLine("轻量单 EXE：使用 Windows 10 1903+ / Windows 11 内置的 .NET Framework 4.8");
        content.AppendLine("私有托管依赖直接嵌入本 EXE，不需要额外 DLL 或 .NET 8 安装");
#else
        content.AppendLine(".NET 8 兼容构建");
#endif
        content.AppendLine("仅调用当前选中的官方安装组件；托盘不读取或导出登录令牌");
        content.AppendLine("官方组件可能自行续期并更新已有登录；不复制官方程序，不跨来源回退");
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("Legal.", StringComparison.Ordinal)).OrderBy(name => name))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            content.AppendLine().AppendLine("===== " + name.Substring(6) + " =====").AppendLine(reader.ReadToEnd());
        }
        Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both, WordWrap = false,
            Text = content.ToString(), Font = new Font("Consolas", 9)
        });
    }
}
