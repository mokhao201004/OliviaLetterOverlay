using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OliviaLetterOverlay;

internal static class RegressionTests
{
    private static int _passed;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--ui"))
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }

        try
        {
            if (args.Contains("--verify-restart"))
            {
                Check(CharacterStore.Current.Name == "海风", "active character survives restart");
                Check(PersonaStore.Load()!.Memories.Single() == "B：周六去潜水，喜欢咸柠檬。", "memory survives restart without A");
                return 0;
            }

            if (args.Contains("--verify-title-restart"))
            {
                var character = CharacterStore.List().Single(item => item.Name == "星河");
                Check(LetterTitleStore.Load(character.Id).ContainsValue("星空日记"), "saved record name survives restart");
                Check(LetterTitleStore.Load(CharacterStore.DefaultId)[LetterTitleStore.HelloKey] == "第一封问候", "built-in record name survives restart");
                return 0;
            }

            if (args.Contains("--verify-reply-tone"))
            {
                TestLetterQualityCheck();
                TestReplyToneGuidance();
                return 0;
            }

            TestCharacters();
            TestLetterTitles();
            TestRequestsAsync().GetAwaiter().GetResult();
            TestReplyResponsesAsync().GetAwaiter().GetResult();
            TestDownloadsAsync().GetAwaiter().GetResult();
            TestDiagnostics();
            TestLetterTitleControls();
            TestTtsClient();
            TestStyleMemoryMerge();
            TestLetterQualityCheck();
            TestReplyToneGuidance();
            Console.WriteLine($"PASS: {_passed} checks; isolated fixtures: {Environment.TestRoot}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void TestTtsClient()
    {
        Check(IndexTtsClient.CacheFileName("ABC-def123", "你好", "") == "abcdef123", "record keys become safe cache names");
        Check(IndexTtsClient.CacheFileName(null, "  同一封信  ", "") == IndexTtsClient.CacheFileName(null, "同一封信", ""),
            "reply-only cache keys hash the trimmed reply");
        Check(IndexTtsClient.CacheFileName(null, "A", "").StartsWith("adhoc-"), "reply-only cache keys are marked adhoc");
        Check(IndexTtsClient.CacheFileName("abc", "x", "feed1234") == "abc-feed1234", "cache keys carry the settings fingerprint");
        Check(IndexTtsClient.StripSignature("好的，晚安。\n\n—— 林离") == "好的，晚安。", "trailing em-dash signature is not read aloud");
        Check(IndexTtsClient.StripSignature("第一段。\n\n第二段。\n\n—— 星河\n") == "第一段。\n\n第二段。", "signature with trailing blank line is stripped");
        Check(IndexTtsClient.StripSignature("没有署名的信") == "没有署名的信", "letters without signatures are unchanged");
        Check(IndexTtsClient.StripSignature("—— 林离").Length == 0, "signature-only letters strip to empty");

        var (fileName, arguments) = IndexTtsClient.BuildWorkerCommand(
            @"X:\py.exe", @"X:\worker.py", @"X:\in.txt", @"X:\out.wav", @"X:\report.json", @"X:\ref.wav", 20260830, "staged", 200, 120, 1.0);
        Check(fileName == @"X:\py.exe", "worker runs with the configured python interpreter");
        var expectedArguments = new[]
        {
            @"X:\worker.py", "--text-file", @"X:\in.txt", "--output", @"X:\out.wav", "--reference", @"X:\ref.wav",
            "--seed", "20260830", "--mode", "staged", "--interval-silence", "200", "--max-text-tokens", "120",
            "--duration-factor", "1", "--report", @"X:\report.json",
        };
        Check(arguments.SequenceEqual(expectedArguments), "worker arguments carry text/output/reference/seed/mode/report");

        var defaultRoot = new TtsPreferences().IndexTtsRoot;
        var pythonPath = Path.Combine(defaultRoot, ".venv", "Scripts", "python.exe");
        var workerScript = Path.Combine(defaultRoot, "local_tools", "olivia_tts_worker.py");
        var referencePath = Path.Combine(defaultRoot, "reference", "lv_0_reference_6.8-22.1.wav");
        if (File.Exists(pythonPath) && File.Exists(workerScript) && File.Exists(referencePath))
        {
            var textFile = Path.Combine(Path.GetTempPath(), $"olivia-tts-dryrun-{Guid.NewGuid():N}.txt");
            var reportFile = textFile + ".report.json";
            File.WriteAllText(textFile, "测试", Encoding.UTF8);
            try
            {
                var (dryFileName, dryArguments) = IndexTtsClient.BuildWorkerCommand(
                    pythonPath, workerScript, textFile, textFile + ".wav", reportFile, referencePath, 20260830, "staged",
                    200, 120, 1.0, dryRun: true);
                var startInfo = new ProcessStartInfo
                {
                    FileName = dryFileName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                foreach (var argument in dryArguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo)!;
                var output = process.StandardOutput.ReadToEnd();
                var dryError = process.StandardError.ReadToEnd();
                process.WaitForExit(60000);
                Check(process.ExitCode == 0, "tts worker dry-run exits 0");
                Check(output.Contains("DRY-RUN OK"), $"tts worker dry-run confirms arguments; stdout=[{output}] stderr=[{dryError}]");
                Check(File.Exists(reportFile), "tts worker dry-run writes a report");
            }
            finally
            {
                File.Delete(textFile);
                File.Delete(textFile + ".wav");
                if (File.Exists(reportFile))
                {
                    File.Delete(reportFile);
                }
            }
        }
    }

    private static void TestLetterQualityCheck()
    {
        var clean = "今天练完了。就是这样。\n\n—— 林离";
        Check(LetterQualityCheck.Validate(clean, "林离").Count == 0, "clean letter passes quality check");

        var issues = LetterQualityCheck.Validate("我理解你。*抱抱*你要加油。", "林离");
        Check(issues.Count >= 3, "banned phrases and stage directions are flagged");
        Check(issues.Any(issue => issue.Contains("星号")), "stage direction issue is reported");
        Check(issues.Any(issue => issue.Contains("套话")), "banned phrase issue is reported");

        var templated = LetterQualityCheck.Validate("收到你的信了。无论怎样，你都不是一个人。\n\n—— 林离", "林离");
        Check(templated.Any(issue => issue.Contains("严重模板化")), "generic letter template is a high-penalty violation");

        var emotionless = LetterQualityCheck.Validate("明天可以早点睡。\n\n—— 林离", "林离", requireEmotion: true);
        Check(emotionless.Any(issue => issue.Contains("严重情绪缺失")), "emotionally relevant letters reject detached replies");
        var responsive = LetterQualityCheck.Validate("听到你这么累，我心里也有点不踏实。今晚先别逼自己把所有事理顺。\n\n—— 林离", "林离", requireEmotion: true);
        Check(!responsive.Any(issue => issue.Contains("严重情绪缺失")), "specific emotional response is not penalized");
        Check(!LetterQualityCheck.IsRepairImproved(["严重情绪缺失：缺少回应", "缺少落款"], ["严重情绪缺失：仍然缺少回应"]),
            "repair that keeps a high-penalty emotional failure is rejected");
        Check(LetterQualityCheck.IsRepairImproved(["严重情绪缺失：缺少回应", "缺少落款"], ["缺少落款"]),
            "repair that clears a high-penalty emotional failure is accepted");

        Check(LetterQualityCheck.Validate("没有落款的信", "林离").Any(issue => issue.Contains("落款")), "missing signature is flagged");
        Check(LetterQualityCheck.Validate("开头就写林离觉得如何。\n\n—— 林离", "林离").Any(issue => issue.Contains("正文中途")), "mid-text character name is flagged");

        var repair = LetterQualityCheck.BuildRepairMessages(
            new List<object> { new { role = "system", content = "s" } },
            "旧稿内容",
            new List<string> { "问题一", "问题二" });
        Check(repair.Count == 3, "repair messages append assistant draft and user correction");
    }

    private static void TestReplyToneGuidance()
    {
        Check(PersonaPrompt.System.Contains("先接住对方当下的情绪"), "reply prompt requires acknowledging the reader's current emotion first");
        Check(PersonaPrompt.System.Contains("让对方能感觉到你是在意的"), "reply prompt requires visible but restrained care");
        Check(PersonaPrompt.System.Contains("不夸张煽情"), "reply prompt keeps emotional care from becoming melodramatic");
        Check(PersonaPrompt.System.Contains("不必把每句话说得面面俱到"), "reply prompt permits natural, non-formulaic wording");
        Check(PersonaPrompt.System.Contains("有明确的偏向"), "reply prompt asks for a personal point of view instead of neutral answers");
        Check(PersonaPrompt.System.Contains("模板高惩罚"), "reply prompt treats formulaic writing as a high-priority failure");
        Check(PersonaPrompt.System.Contains("情绪缺失高惩罚"), "reply prompt treats detached replies as a high-priority failure");
    }

    private static void TestStyleMemoryMerge()
    {
        Check(RareCharGuard.IsCommon('的') && RareCharGuard.IsCommon('，'), "common characters pass the whitelist");
        Check(!RareCharGuard.IsCommon('椐') && !RareCharGuard.IsCommon('捃'), "rare confusable characters are flagged");
        Check(RareCharGuard.ReplaceKnownConfusions("椐你一句，捃起来") == "据你一句，拾起来", "known confusions are auto-corrected");

        var existing = new List<string>
        {
            "用户说话：句子短，少标点",
            "用户说话：爱用语气词",
        };
        var merged = UserStyleStore.Merge(existing, "用户说话：爱用省略号", 5);
        Check(merged.First() == "用户说话：爱用省略号", "newest style observation comes first in its own store");
        Check(merged.Skip(1).SequenceEqual(existing), "older style observations stay in their own store");

        var rolled = UserStyleStore.Merge(existing, "用户说话：新的观察", 2);
        Check(rolled.Count == 2 && rolled.First() == "用户说话：新的观察", "style store respects the configured limit");

        var unlimited = UserStyleStore.Merge(existing, "用户说话：新的观察", 0);
        Check(unlimited.Count == 3, "zero style limit keeps every observation");

        var deduped = UserStyleStore.Merge(existing, "用户说话：句子短，少标点", 0);
        Check(deduped.Count(item => item == "用户说话：句子短，少标点") == 1, "duplicate observation is not stored twice");

        var character = CharacterStore.Create("风格迁移", "");
        PersonaStore.Save(new PersonaProfile { Memories = ["事实：喜欢红茶", "用户说话：句子短"] }, character.Id);
        UserStyleStore.MigrateLegacyEntries(character.Id);
        Check(PersonaStore.Load(character.Id)!.Memories.SequenceEqual(["事实：喜欢红茶"]), "legacy style observations leave the memory library");
        Check(UserStyleStore.Load(character.Id).SequenceEqual(["用户说话：句子短"]), "legacy style observations move to the separate store");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        _passed++;
        Console.WriteLine("PASS: " + message);
    }

    private static void TestCharacters()
    {
        Check(CharacterStore.Current.Id == CharacterStore.DefaultId, "legacy install defaults to Lin");
        PersonaStore.Save(new PersonaProfile { Prompt = "原人设", Memories = ["旧记忆保持不变"] });
        LetterStore.Save([new() { Draft = "原来的信", Reply = "原来的回信" }]);
        var legacyPath = Path.Combine(CharacterStore.GetDataDirectory(), "persona-profile.json");
        var legacyBytes = File.ReadAllBytes(legacyPath);
        var roleA = CharacterStore.Create("星河", "A：天文馆讲解员，语气安静。");
        Check(PersonaStore.Load()!.Memories.Count == 0 && LetterStore.Load().Count == 0, "new role starts without legacy memories or history");
        PersonaStore.Save(new PersonaProfile
        {
            Prompt = "A：天文馆讲解员，语气安静。",
            Memories = ["A：周五看星星，喜欢桂花茶。"],
            ReferenceLetters = [new() { Draft = "A参考来信", Reply = "A参考回信" }],
        });
        LetterStore.Save([new() { Draft = "A：今晚想看猎户座。", Reply = "A：我们记得带望远镜。", CreatedAt = DateTime.Now }]);
        AutoLetterStore.Save(new AutoLetterSettings { IntervalMinutes = 10, LastSentAt = DateTime.Now });
        var roleB = CharacterStore.Create("海风", "B：海边的潜水教练，语气开朗。");
        Check(PersonaStore.Load()!.Memories.Count == 0 && LetterStore.Load().Count == 0 && AutoLetterStore.Load().IntervalMinutes == 0,
            "B starts empty with proactive letters disabled");
        PersonaStore.Save(new PersonaProfile { Prompt = "B：海边的潜水教练，语气开朗。", Memories = ["B：周六去潜水，喜欢咸柠檬。"] });
        LetterStore.Save([new() { Draft = "B：周末想去海边。", Reply = "B：记得带潜水镜。", CreatedAt = DateTime.Now }]);
        CharacterStore.Select(roleA.Id);
        Check(PersonaStore.Load()!.Memories.Single().Contains("看星星") && LetterStore.Load().Single().Draft.StartsWith("A："), "switch back restores only A data");
        Check(AutoLetterStore.Load().IntervalMinutes == 10, "proactive settings isolated by role");
        CharacterStore.Select(roleB.Id);
        Check(PersonaStore.Load()!.Memories.Single().Contains("潜水") && LetterStore.Load().Single().Draft.StartsWith("B："), "B remains independent");
        Check(File.ReadAllBytes(legacyPath).SequenceEqual(legacyBytes), "legacy profile is byte-for-byte unchanged");
        Check(PersonaStore.Load(CharacterStore.DefaultId)!.Memories.Single() == "旧记忆保持不变", "legacy memory remains accessible");
        var staleA = PersonaStore.Load(roleA.Id)!;
        staleA.Memories.Add("A：延迟返回的分析结果。");
        PersonaStore.Save(staleA, roleA.Id);
        Check(PersonaStore.Load()!.Memories.Count == 1, "late A save cannot write into active B");
        try { CharacterStore.GetDataDirectory("../../outside"); throw new Exception("Path traversal was accepted"); }
        catch (InvalidOperationException) { Check(true, "invalid role directory is rejected"); }
        var processInfo = new ProcessStartInfo(System.Environment.ProcessPath!, "--verify-restart") { UseShellExecute = false, RedirectStandardOutput = true };
        processInfo.Environment["OLIVIA_TEST_ROOT"] = Environment.TestRoot;
        using var child = Process.Start(processInfo)!;
        var output = child.StandardOutput.ReadToEnd();
        child.WaitForExit();
        Check(child.ExitCode == 0 && output.Contains("survives restart"), "second process reloads the correct role and memories");
        var catalogPath = Path.Combine(Path.GetDirectoryName(legacyPath)!, "characters.json");
        var catalog = File.ReadAllText(catalogPath);
        File.WriteAllText(catalogPath, "{corrupt");
        try { CharacterStore.Create("不能创建", ""); throw new Exception("Corrupt catalog was overwritten"); }
        catch (InvalidOperationException) { Check(File.ReadAllText(catalogPath) == "{corrupt", "corrupt catalog is not silently reset"); }
        finally { File.WriteAllText(catalogPath, catalog); }
    }

    private static void TestLetterTitles()
    {
        var roleA = CharacterStore.List().Single(item => item.Name == "星河");
        var roleB = CharacterStore.List().Single(item => item.Name == "海风");
        Check(LetterTitleStore.Load(roleA.Id).Count == 0, "old history needs no title migration");
        var letters = LetterStore.Load(roleA.Id);
        letters[0].Subject = "你好";
        letters.Add(new SavedLetter { Subject = "你好", Draft = "A：另一封来信", Reply = "A：另一封回信" });
        LetterStore.Save(letters, roleA.Id);
        var originalHistory = File.ReadAllBytes(Path.Combine(CharacterStore.GetDataDirectory(roleA.Id), "letters.json"));
        var firstKey = letters[0].Id.ToString("N");
        var secondKey = letters[1].Id.ToString("N");
        LetterTitleStore.Save(roleA.Id, firstKey, "  星空日记  ");
        Check(LetterTitleStore.Load(roleA.Id)[firstKey] == "星空日记", "record rename trims and saves its display name");
        Check(!LetterTitleStore.Load(roleA.Id).ContainsKey(secondKey), "same original title does not rename another record");
        LetterTitleStore.Save(roleA.Id, secondKey, "另一场闲聊");
        Check(LetterTitleStore.Load(roleA.Id)[firstKey] == "星空日记", "renaming a second record preserves the first name");
        Check(LetterTitleStore.Load(roleB.Id).Count == 0 && CharacterStore.Current.Id == roleB.Id, "record names remain scoped to their role without switching it");
        Check(File.ReadAllBytes(Path.Combine(CharacterStore.GetDataDirectory(roleA.Id), "letters.json")).SequenceEqual(originalHistory), "renaming does not rewrite history content, IDs or dates");

        var defaultHistory = File.ReadAllBytes(Path.Combine(CharacterStore.GetDataDirectory(CharacterStore.DefaultId), "letters.json"));
        LetterTitleStore.Save(CharacterStore.DefaultId, LetterTitleStore.HelloKey, "第一封问候");
        LetterTitleStore.Save(CharacterStore.DefaultId, LetterTitleStore.WelcomeKey, "欢迎信");
        Check(LetterTitleStore.Load(CharacterStore.DefaultId)[LetterTitleStore.HelloKey] == "第一封问候"
            && LetterTitleStore.Load(CharacterStore.DefaultId)[LetterTitleStore.WelcomeKey] == "欢迎信", "both built-in records have independent names");
        Check(File.ReadAllBytes(Path.Combine(CharacterStore.GetDataDirectory(CharacterStore.DefaultId), "letters.json")).SequenceEqual(defaultHistory), "renaming a built-in does not duplicate it in saved history");
        foreach (var invalidTitle in new[] { "   ", new string('字', 41), "一行\n另一行" })
        {
            try { LetterTitleStore.Save(roleA.Id, firstKey, invalidTitle); throw new Exception("Invalid name was accepted"); }
            catch (InvalidOperationException) { Check(LetterTitleStore.Load(roleA.Id)[firstKey] == "星空日记", "invalid record name does not overwrite the saved name"); }
        }

        var start = new ProcessStartInfo(System.Environment.ProcessPath!, "--verify-title-restart") { UseShellExecute = false, RedirectStandardOutput = true };
        start.Environment["OLIVIA_TEST_ROOT"] = Environment.TestRoot;
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEnd();
        child.WaitForExit();
        Check(child.ExitCode == 0 && output.Contains("built-in record name survives restart"), "second process reloads normal and built-in record names");
        var titlePath = Path.Combine(CharacterStore.GetDataDirectory(roleA.Id), "letter-titles.json");
        var originalTitles = File.ReadAllText(titlePath);
        File.WriteAllText(titlePath, "{corrupt");
        try { LetterTitleStore.Save(roleA.Id, firstKey, "不能覆盖"); throw new Exception("Corrupt names were overwritten"); }
        catch (InvalidOperationException) { Check(File.ReadAllText(titlePath) == "{corrupt", "corrupt record names are not silently reset"); }
        finally { File.WriteAllText(titlePath, originalTitles); }
    }

    private static void TestLetterTitleControls()
    {
        // Exercise the actual WPF controls without showing a window or taking desktop focus.
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        CharacterStore.Select(CharacterStore.DefaultId);
        var window = new MainWindow();
        try
        {
            static void Click(System.Windows.Controls.Button button) => button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Click(window.HelloItem);
            var replyText = window.ReplyTextBlock.Text;
            Click(window.LetterTitleButton);
            window.LetterTitleBox.Text = "初次问候";
            Click(window.SaveLetterTitleButton);
            Check(window.HelloTitleText.Text == "初次问候" && window.SubjectText.Text == "初次问候", "renaming Hello updates sidebar and selected title together");
            Check(window.ReplyTextBlock.Text == replyText && window.MailboxTitleText.Text.StartsWith("我的信箱"), "rename preserves letter body and mailbox heading");
            Click(window.WelcomeItem);
            Check(window.SubjectText.Text == "欢迎信", "selecting another built-in keeps its own name");
            Click(window.HelloItem);
            Check(window.SubjectText.Text == "初次问候", "reselecting a renamed record preserves its name");
            Click(window.LetterTitleButton);
            window.LetterTitleBox.Text = "不应保存";
            Click(window.CancelLetterTitleButton);
            Check(window.SubjectText.Text == "初次问候" && LetterTitleStore.Load(CharacterStore.DefaultId)[LetterTitleStore.HelloKey] == "初次问候", "cancel leaves the saved record name unchanged");
            Check(window.LetterTitleEditor.Visibility == System.Windows.Visibility.Collapsed, "cancel closes the rename editor");
            var savedItem = (System.Windows.Controls.Button)window.SavedLettersPanel.Children[0];
            Click(savedItem);
            Click(window.LetterTitleButton);
            window.LetterTitleBox.Text = "一段闲聊";
            Click(window.SaveLetterTitleButton);
            Check(window.SubjectText.Text == "一段闲聊" && window.HelloTitleText.Text == "初次问候", "normal history rename does not rename built-in Hello");
            Check(window.HelloItem.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single().Header as string == "重命名", "sidebar records expose a rename menu");
            TestReplyRendering(window);
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
    }

    private static void TestReplyRendering(MainWindow window)
    {
        var body = string.Join("\n\n", Enumerable.Range(1, 20).Select(index =>
            $"第{index}段：周末的风从窗边吹过，我把这一路看到的云和树都慢慢写下来。这一段应当完整留在信纸上，不应该因为篇幅变长就被省略。")) + "\n\n";
        var longReply = body + "这是最后一行，收尾验证甲。";
        var otherEnding = body + "这是最后一行，收尾验证乙。";
        var size = new System.Windows.Size(554, 310);
        var shortPages = ReplyLetterRenderer.RenderPages("一封短回信。\n—— 海风", size);
        var pages = ReplyLetterRenderer.RenderPages(longReply, size);
        var otherPages = ReplyLetterRenderer.RenderPages(otherEnding, size);
        var pageTexts = ReplyLetterRenderer.Paginate(longReply, size);
        Check(shortPages.Count == 1 && shortPages[0].Width == 554 && shortPages[0].Height == 310, "short reply keeps one original-size paper");
        Check(pages.Count > 1 && pages.All(page => page.Width == 554 && page.Height == 310), "long reply uses multiple original-size pages, not a stretched sheet");
        Check(string.Concat(pageTexts) == longReply, "pagination preserves every character and paragraph break");
        Check(pageTexts.All(page => ReplyLetterRenderer.FormatReply(page, size.Width).Height <= size.Height * .74), "every page's full text fits within the actual paper writing area");
        var mixedText = string.Concat(Enumerable.Repeat("中文👩‍👩‍👧‍👧e\u0301English_very_long_unbroken_word" + new string('x', 180) + "\r\n\r\n", 10));
        var mixedPages = ReplyLetterRenderer.Paginate(mixedText, size);
        var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(mixedText).Append(mixedText.Length).ToHashSet();
        var offset = 0;
        Check(string.Concat(mixedPages) == mixedText && mixedPages.All(page => { offset += page.Length; return boundaries.Contains(offset); }), "mixed scripts, emoji, combining characters and line breaks survive pagination");
        Check(mixedPages.All(page => ReplyLetterRenderer.FormatReply(page, size.Width).Height <= size.Height * .74), "unbroken English and emoji pages fit the writing area");
        var image = pages[^1];
        var otherImage = otherPages[^1];
        Check(pages.Count == otherPages.Count, "different final characters retain the same page count");
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        var otherPixels = new byte[pixels.Length];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        otherImage.CopyPixels(otherPixels, image.PixelWidth * 4, 0);
        Check(!pixels.SequenceEqual(otherPixels), "the final line is actually painted on the last page");

        var sent = SentLetterRenderer.Render("测试信封", size, "2026-08-28");
        var combined = LetterExport.Combine(pages, sent);
        Check(combined.Width == 554 && combined.Height == (pages.Count + 1) * 310 + pages.Count * 9, "sharing includes every page in order plus the envelope");
        Check(LetterExport.Combine(shortPages, sent).Height == 629, "short-letter sharing retains the previous layout");
        var previewPath = Path.Combine(Environment.TestRoot, "reply-last-page.png");
        LetterExport.SavePng(image, previewPath);
        LetterExport.SavePng(pages[0], Path.Combine(Environment.TestRoot, "reply-first-page.png"));
        LetterExport.SavePng(combined, Path.Combine(Environment.TestRoot, "reply-pages-share.png"));
        LetterExport.SavePair(sent, pages, Path.Combine(Environment.TestRoot, "download-fixture.png"), "海风");
        Check(Directory.GetFiles(Environment.TestRoot, "download-fixture-*.png").Length == pages.Count + 1
            && File.Exists(Path.Combine(Environment.TestRoot, $"download-fixture-海风回信-第{pages.Count}页.png")), "download saves all numbered reply pages and the envelope");
        using (var stream = File.OpenRead(previewPath))
        {
            var decoded = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            Check(decoded.PixelHeight == 620 && decoded.PixelWidth == 1108, "downloaded page preserves original dimensions at 2x resolution");
        }

        typeof(MainWindow).GetMethod("ShowLetter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, ["长回信测试", "2026-08-28 18:00", "测试来信", longReply, null]);
        var root = (System.Windows.FrameworkElement)window.Content;
        root.Measure(new System.Windows.Size(window.Width, window.Height));
        root.Arrange(new System.Windows.Rect(0, 0, window.Width, window.Height));
        root.UpdateLayout();
        Check(window.ReplyPaperStack.Height == 310 && window.ReplyPaperStack.ActualHeight == 310, "reader keeps the paper viewport at one page height");
        Check(window.ReplyTextBlock.ActualHeight > 310, "long letter text layer is taller than the viewport");
        Check(window.ReplyScroll.ScrollableHeight > 0, "wheel scrolls only the text layer inside the fixed frame");
        Check(!string.IsNullOrWhiteSpace(window.LetterDateText.Text), "letter date is pinned on the paper");
        root.UpdateLayout();
        var screenshot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        screenshot.Render(root);
        LetterExport.SavePng(screenshot, Path.Combine(Environment.TestRoot, "stacked-reply-window.png"));
        window.HelloItem.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        // 短信重置检查使用显式的单行短信，不依赖任何字号下的实际分页结果。
        typeof(MainWindow).GetMethod("ShowLetter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, ["短信重置", "2026-08-28 18:05", "短信来信", "晚安。记得盖好被子。", null]);
        root.UpdateLayout();
        Check(window.ReplyPaperStack.Height == 310 && window.ReplyScroll.ScrollableHeight == 0
            && window.ReplyTextBlock.Text == "晚安。记得盖好被子。", "switching to a short letter resets the text layer to a single unscrollable sheet");
    }
    private static async Task TestRequestsAsync()
    {
        var roleA = CharacterStore.List().Single(character => character.Name == "星河");
        var roleB = CharacterStore.List().Single(character => character.Name == "海风");
        foreach (var role in new[] { roleA, roleB })
        {
            CharacterStore.Select(role.Id);
            using var server = new LocalResponseServer("{\"choices\":[{\"message\":{\"content\":\"收到来信。\"}}]}");
            ConfigureApi(server.Url);
            await MimoClient.GenerateReplyAsync("测试新信", LetterStore.Load(), role.Id);
            var request = await server.Request;
            var otherMarker = role.Id == roleA.Id ? "B：" : "A：";
            var ownMarker = role.Id == roleA.Id ? "A：" : "B：";
            Check(request.Contains(ownMarker) && !request.Contains(otherMarker), $"{role.Name} outgoing request has only own persona, memory and history");
            Check(!request.Contains("上海高校在读") && !request.Contains("—— 林离"), "custom role does not inherit default identity");
        }

        CharacterStore.Select(roleA.Id);
        var delayedReply = "星河，" + new string('好', 300) + "。\n—— 星河";
        using (var server = new LocalResponseServer(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = delayedReply } } } }), delayMs: 180))
        {
            ConfigureApi(server.Url);
            var pending = MimoClient.GenerateReplyAsync("测试延迟", LetterStore.Load(), roleA.Id);
            await server.Request;
            CharacterStore.Select(roleB.Id);
            var reply = await pending;
            Check(reply.EndsWith("—— 星河"), "in-flight A reply keeps A signature after switching to B");
            Check(reply == delayedReply[3..], "late reply cleanup uses the request's role and preserves the complete body");
        }

        foreach (var operation in new[] { "proactive", "memory" })
        {
            using var server = new LocalResponseServer("{\"choices\":[{\"message\":{\"content\":\"{\\\"memories\\\":[\\\"B：潜水\\\"]}\"}}]}");
            ConfigureApi(server.Url);
            if (operation == "proactive") await MimoClient.GenerateProactiveLetterAsync(LetterStore.Load(roleB.Id), roleB.Id);
            else await MimoClient.AnalyzeMemoriesAsync(LetterStore.Load(roleB.Id));
            var request = await server.Request;
            Check(request.Contains("B：") && !request.Contains("A："), operation + " only sends B history");
        }
    }

    private static async Task TestReplyResponsesAsync()
    {
        var longReply = string.Concat(Enumerable.Repeat("这是不能被截掉的完整回信。", 50)) + "\n最后一句也要保留。\n—— 海风";
        using (var server = new LocalResponseServer(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = longReply }, finish_reason = "stop" } },
        })))
        {
            ConfigureApi(server.Url);
            var reply = await MimoClient.GenerateReplyAsync("请写完整", []);
            Check(reply == longReply, "long reply preserves all text and the real ending");
            using var request = JsonDocument.Parse(await server.Request);
            Check(request.RootElement.GetProperty("max_tokens").GetInt32() == 4096, "reply has enough output budget instead of 300 tokens");
            Check(!request.RootElement.TryGetProperty("thinking", out _), "custom endpoints do not receive provider-specific thinking parameters");
            Check(!request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("120–220")
                && !PersonaPrompt.System.Contains("180–260"), "reply prompts no longer enforce a fixed word count");
        }

        foreach (var provider in new[] { "deepseek", "zhipu" })
        {
            using var server = new LocalResponseServer("{\"choices\":[{\"message\":{\"content\":\"完整的主动来信。\"},\"finish_reason\":\"stop\"}]}");
            var settings = new AiProviderSettings { Provider = AiProviderKind.OpenAiCompatible, CloudProviderId = provider, BaseUrl = server.Url, Model = "fixture-model" };
            AiProviderStore.SaveCompatibleApiKey(settings, "fixture-private-key-12345");
            AiProviderStore.Save(settings);
            await MimoClient.GenerateProactiveLetterAsync([]);
            using var request = JsonDocument.Parse(await server.Request);
            Check(request.RootElement.GetProperty("max_tokens").GetInt32() == 4096, provider + " proactive letters use the same output budget");
            Check(provider == "deepseek"
                ? request.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled"
                : !request.RootElement.TryGetProperty("thinking", out _) && request.RootElement.GetProperty("reasoning_effort").GetString() == "low",
                provider + " uses supported thinking controls without disabling GLM's required thinking");
        }

        foreach (var item in new[]
        {
            ("reasoning exhausted budget", "{\"choices\":[{\"message\":{\"content\":\"\",\"reasoning_content\":\"PRIVATE_REASONING_MUST_NOT_EXPORT\"},\"finish_reason\":\"length\"}]}", "长度上限"),
            ("partial answer", "{\"choices\":[{\"message\":{\"content\":\"PRIVATE_PARTIAL_ANSWER\"},\"finish_reason\":\"length\"}]}", "长度上限"),
            ("server interrupted", "{\"choices\":[{\"message\":{\"content\":\"PRIVATE_PARTIAL_ANSWER\"},\"finish_reason\":\"insufficient_system_resource\"}]}", "服务器资源不足"),
            ("reasoning only", "{\"choices\":[{\"message\":{\"content\":null,\"reasoning_content\":\"PRIVATE_REASONING_MUST_NOT_EXPORT\"},\"finish_reason\":\"stop\"}]}", "思考"),
            ("empty answer", "{\"choices\":[{\"message\":{\"content\":\"  \"},\"finish_reason\":\"stop\"}]}", "正文"),
            ("content filter", "{\"choices\":[{\"message\":{\"content\":null},\"finish_reason\":\"content_filter\"}]}", "过滤"),
            ("tool call instead of answer", "{\"choices\":[{\"message\":{\"content\":null},\"finish_reason\":\"tool_calls\"}]}", "工具"),
            ("wrong response format", "{\"data\":[]}", "格式"),
            ("malformed message", "{\"choices\":[{\"message\":[],\"finish_reason\":\"stop\"}]}", "格式"),
            ("only metadata", "{\"choices\":[{\"message\":{\"content\":\"天气：晴\\n心情：平静\"},\"finish_reason\":\"stop\"}]}", "正文"),
        })
        {
            using var server = new LocalResponseServer(item.Item2);
            ConfigureApi(server.Url);
            Exception? failure = null;
            try { await MimoClient.GenerateReplyAsync("测试", []); }
            catch (Exception exception) { failure = exception; }
            Check(failure is InvalidOperationException && failure.Message.Contains(item.Item3), item.Item1 + " has a specific error and is not accepted as a complete reply");
            Check(!failure!.Message.Contains("PRIVATE_"), item.Item1 + " does not expose response bodies in error messages");
        }

        using (var server = new LocalResponseServer("{\"choices\":[{\"message\":{\"content\":[null,3,{\"type\":\"reasoning\",\"text\":\"PRIVATE_REASONING_MUST_NOT_EXPORT\"},{\"type\":\"text\",\"text\":\"第一段。\"},{\"type\":\"output_text\",\"text\":\"第二段。\"},{\"text\":3},\"第三段。\"]},\"finish_reason\":\"stop\"}]}"))
        {
            ConfigureApi(server.Url);
            Check(await MimoClient.GenerateReplyAsync("测试文本块", []) == "第一段。第二段。第三段。", "text blocks are joined without malformed values or reasoning blocks");
        }

        foreach (var item in new[]
        {
            ("complete", true, "stop", "完整的本地回信。", false),
            ("truncated", true, "length", "PRIVATE_PARTIAL_ANSWER", true),
            ("unfinished", false, "", "PRIVATE_PARTIAL_ANSWER", true),
            ("thinking only", true, "stop", "", true),
        })
        {
            using var server = new LocalResponseServer(JsonSerializer.Serialize(new
            {
                message = new { content = item.Item4, thinking = "PRIVATE_REASONING_MUST_NOT_EXPORT" },
                done = item.Item2,
                done_reason = item.Item3,
            }));
            AiProviderStore.Save(new AiProviderSettings { Provider = AiProviderKind.Ollama, BaseUrl = server.Url, Model = "fixture-model" });
            Exception? failure = null;
            try { Check(await MimoClient.GenerateReplyAsync("测试", []) == item.Item4, "Ollama complete content is unchanged"); }
            catch (Exception exception) { failure = exception; }
            Check((failure is InvalidOperationException) == item.Item5, "Ollama response: " + item.Item1);
            using var request = JsonDocument.Parse(await server.Request);
            Check(request.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32() == 4096, "Ollama reply output budget is not 300");
        }
    }

    private static void ConfigureApi(string url)
    {
        var settings = new AiProviderSettings { Provider = AiProviderKind.OpenAiCompatible, CloudProviderId = "custom", BaseUrl = url, Model = "fixture-model" };
        AiProviderStore.SaveCompatibleApiKey(settings, "fixture-private-key-12345");
        AiProviderStore.Save(settings);
    }

    private static async Task TestDownloadsAsync()
    {
        foreach (var item in new[]
        {
            ("success", 200, "{\"status\":\"success\"}\n", false),
            ("disk full", 200, "{\"status\":\"pulling model\",\"completed\":7,\"total\":100}\n{\"error\":\"There is not enough space on the disk.\"}\n", true),
            ("missing final success", 200, "{\"status\":\"pulling manifest\"}\n", true),
            ("missing model", 404, "{\"error\":\"model does not exist\"}", true),
        })
        {
            using var server = new LocalResponseServer(item.Item3, item.Item2);
            Exception? failure = null;
            try { await OllamaClient.PullModelAsync(server.Url, "fixture-model", null, CancellationToken.None); }
            catch (Exception exception) { failure = exception; }
            Check((failure is not null) == item.Item4, "download result: " + item.Item1);
            if (item.Item1 == "disk full") Check(failure!.Message.Contains("not enough space"), "download error is preserved");
        }

        using var trace = new DiagnosticLog.DownloadTrace("stalled-fixture-model");
        trace.Update("pulling model", "sha256:test", 7, 100);
        typeof(DiagnosticLog.DownloadTrace).GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(trace, null);
        trace.Finish("cancelled by test");
    }

    private static void TestDiagnostics()
    {
        DiagnosticLog.RegisterSecret("private-test-key-77");
        var redacted = DiagnosticLog.Redact("Bearer unknown-secret api_key=other-secret https://user:pass@example.com/v1?token=private-test-key-77 " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Check(!redacted.Contains("unknown-secret") && !redacted.Contains("other-secret") && !redacted.Contains("private-test-key") && !redacted.Contains("user:pass") && !redacted.Contains("?token"), "keys, URL credentials and query strings are redacted");
        Check(redacted.Contains("%USERPROFILE%"), "local user directory is redacted");
        var ollamaDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama");
        Directory.CreateDirectory(ollamaDirectory);
        File.WriteAllText(Path.Combine(ollamaDirectory, "server.log"), "PRIVATE_CHAT_MUST_NOT_EXPORT\nsource=download.go:1 part stalled https://example.com/blob?token=private-test-key-77\n");
        DiagnosticLog.Write("test", "private-test-key-77");
        var exportPath = Path.Combine(Environment.TestRoot, "diagnostics.txt");
        DiagnosticLog.Export(exportPath);
        var exported = File.ReadAllText(exportPath);
        Check(exported.Contains("part stalled") && exported.Contains("no_progress_seconds=") && exported.Contains("not enough space"), "export includes stalls, byte counters and download errors");
        Check(exported.Contains("HTTP=200") && exported.Contains("HTTP=404"), "export contains actual HTTP outcomes");
        Check(!exported.Contains("PRIVATE_CHAT_MUST_NOT_EXPORT") && !exported.Contains("看星星") && !exported.Contains("咸柠檬") && !exported.Contains("今晚想看猎户座"), "export excludes conversations, memories and Ollama chat logs");
        Check(!exported.Contains("fixture-private-key-12345") && !exported.Contains("private-test-key-77"), "export excludes synthetic API keys");
        Check(exported.Contains("finish_reason=length") && exported.Contains("content_chars=") && exported.Contains("reasoning_chars=") && exported.Contains("outcome=invalid_format"), "export distinguishes truncation, reasoning-only and malformed responses");
        Check(!exported.Contains("PRIVATE_REASONING_MUST_NOT_EXPORT") && !exported.Contains("PRIVATE_PARTIAL_ANSWER"), "response diagnostics contain counts, not reasoning or partial answers");
    }

    private sealed class LocalResponseServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serving;
        public string Url { get; }
        public Task<string> Request => _request.Task;

        public LocalResponseServer(string response, int status = 200, int delayMs = 0)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _serving = ServeAsync(response, status, delayMs);
        }

        private async Task ServeAsync(string response, int status, int delayMs)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var connection = await _listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                var header = new List<byte>();
                var next = new byte[1];
                while (header.Count < 65536)
                {
                    if (await stream.ReadAsync(next, timeout.Token) == 0) throw new IOException("Incomplete request");
                    header.Add(next[0]);
                    if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }

                var headerText = Encoding.ASCII.GetString(header.ToArray());
                var lengthLine = headerText.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, timeout.Token);
                // Decode the JSON so tests assert semantic text rather than JSON escaping.
                var requestText = Encoding.UTF8.GetString(body);
                if (length > 0)
                {
                    using var json = JsonDocument.Parse(requestText);
                    requestText = JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                }

                _request.TrySetResult(requestText);
                if (delayMs > 0) await Task.Delay(delayMs, timeout.Token);
                var responseBytes = Encoding.UTF8.GetBytes(response);
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Result\r\nContent-Type: application/json\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, timeout.Token);
                await stream.WriteAsync(responseBytes, timeout.Token);
            }
            catch (Exception exception) { _request.TrySetException(exception); }
        }

        public void Dispose() => _listener.Stop();
    }
}
