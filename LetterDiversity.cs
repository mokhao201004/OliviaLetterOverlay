namespace OliviaLetterOverlay;

// 信件多样性素材池：样例只供模型学长度、形状与说话质感，内容必须换成对当次来信的回应；
// 素材是可选的生活小事，用不用、用几条都随机。
// 注意：样例必须保持内容中性且不含落款——它们会注入所有角色，不得携带任何角色的身份信息。
internal static class LetterDiversity
{
    public static readonly string[] Exemplars =
    [
        "今天没什么特别的。楼下超市的酸奶补货了，买了两盒。就这样。",
        "你问我是不是不开心。不是的。只是那天事情做到一半突然停住了，坐了一会儿。这种时候不想说话，不是针对谁。",
        "你有没有过那种时候——手在动，脑子在别的地方？我昨天一下午都是这样。你说的那件事，我想了想，你做得没有错。",
        "今天发生了三件事。楼下的猫换了花色（或者来了只新的？）；窗户关不上了，风一吹纸响；晚饭的面比平时咸。你说的事我记下了，等我缓两天再认真回你。",
        "这周都在做同一件事。慢一点，做错就重来。没什么可说的，就是做。",
        "最近雨水多，纸摸上去都是潮的。你上次问的那个不难，难的是做得不像在赶。有空可以试试。",
        "今天不写了，手酸。",
        "刚才想说什么来着。哦，是你说到习惯。习惯这个东西，我不太确定是好事。一半靠习惯活着，另一半得把习惯拆掉。",
        "你写的那段我看了两遍。第二遍比第一遍好。别问我为什么，说不上来。",
        "下午去了趟旧书店，本来想找一本传记，没找到，翻到一本讲录音史的，站了四十分钟，最后没买。就记得这么多，先这样。",
    ];

    public static readonly string[] LifeSeeds =
    [
        "琴房空调坏了，练到一半开着窗吹自然风",
        "楼下面包店提前关门，没买到第二天的早饭",
        "调音师来过，说琴的榔头该整了",
        "下雨没带伞，在门厅等了二十分钟",
        "食堂换了打菜的窗口，队伍排到了门口",
        "谱架的螺丝丢了一颗，先用橡皮筋凑合",
        "傍晚路灯亮得比平时早",
        "吃到了很久没吃的糖醋排骨",
        "学生把同一个音弹错了八遍",
        "翻到一本旧谱子，里面有前一个人写的指法",
        "指甲剪太短，按琴键有点疼",
        "晚上练完发现谱子夹了一根头发",
    ];

    public static string SampleExemplar(Random random) => Exemplars[random.Next(Exemplars.Length)];

    public static List<string> SampleSeeds(Random random)
    {
        var roll = random.Next(4);
        var count = roll == 0 ? 0 : roll == 3 ? 2 : 1;
        var pool = new List<string>(LifeSeeds);
        var picked = new List<string>();
        while (picked.Count < count && pool.Count > 0)
        {
            var index = random.Next(pool.Count);
            picked.Add(pool[index]);
            pool.RemoveAt(index);
        }

        return picked;
    }
}
