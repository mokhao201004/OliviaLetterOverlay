namespace OliviaLetterOverlay;

// 回信质量硬校验 + 一次修复重写（借鉴 linli-local-mail 的 validate/repair 思路，
// 违规清单按本项目的文风规则定制）。修复失败时保留原稿，不让用户干等。
internal static class LetterQualityCheck
{
    public static readonly string[] BannedPhrases =
    [
        "我理解你", "别想太多", "你真的很棒", "相信自己", "一切都会好的",
        "加油", "总而言之", "愿你", "在这个",
    ];

    public static List<string> Validate(string? text, string signatureName)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            issues.Add("正文为空");
            return issues;
        }

        if (text.Contains('*'))
        {
            issues.Add("出现了星号动作标记（角色扮演舞台指示），要删除");
        }

        if (text.Contains('“') || text.Contains('”') || text.Contains('「') || text.Contains('」') || text.Contains('"'))
        {
            issues.Add("出现了引号，引用要改成直接转述");
        }

        foreach (var phrase in BannedPhrases)
        {
            if (text.Contains(phrase, StringComparison.Ordinal))
            {
                issues.Add("出现了套话「" + phrase + "」，要删除或改写成自己的话");
            }
        }

        var signature = "—— " + signatureName;
        var signatureIndex = text.LastIndexOf(signature, StringComparison.Ordinal);
        if (signatureIndex < 0)
        {
            issues.Add("缺少「" + signature + "」落款");
        }
        else
        {
            var nameIndex = text.IndexOf(signatureName, StringComparison.Ordinal);
            if (nameIndex >= 0 && nameIndex < signatureIndex)
            {
                issues.Add("正文中途出现了角色名，角色名只能出现在结尾落款");
            }
        }

        if (text.Length > 1600)
        {
            issues.Add("太长了，要删减到 1600 字以内");
        }

        return issues;
    }

    public static List<object> BuildRepairMessages(List<object> messages, string draft, List<string> issues)
    {
        var repaired = new List<object>(messages)
        {
            new { role = "assistant", content = draft },
            new
            {
                role = "user",
                content = "你上一封回信存在以下问题：\n- " + string.Join("\n- ", issues)
                    + "\n\n请重写一封完整的回信：只修正上面列出的问题，其余内容尽量原样保留，不要延长信件；只输出回信正文本身。",
            },
        };
        return repaired;
    }
}
