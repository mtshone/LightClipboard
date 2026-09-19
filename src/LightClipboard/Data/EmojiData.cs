using System.Collections.Generic;

namespace LightClipboard.Data;

/// <summary>
/// 单个 Emoji 条目：包含 Emoji 字符本身、简短中文名称以及中英文搜索关键词。
/// </summary>
public sealed class EmojiEntry
{
    /// <summary>
    /// 创建一个 Emoji 条目。
    /// </summary>
    /// <param name="value">Emoji 字符本身，例如 "😀"。</param>
    /// <param name="name">简短中文名称，例如 "笑脸"。</param>
    /// <param name="keywords">空格分隔的搜索关键词，包含中文与英文。</param>
    public EmojiEntry(string value, string name, string keywords)
    {
        Value = value;
        Name = name;
        Keywords = keywords;
    }

    /// <summary>
    /// Emoji 字符本身，例如 "😀"。
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// 简短中文名称，例如 "笑脸"。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 空格分隔的搜索关键词，中文与英文混排，例如 "笑脸 高兴 开心 smile happy"。
    /// </summary>
    public string Keywords { get; }
}

/// <summary>
/// Emoji 分类：包含分类名称、代表性图标以及该分类下的全部条目。
/// </summary>
public sealed class EmojiCategory
{
    /// <summary>
    /// 创建一个 Emoji 分类。
    /// </summary>
    /// <param name="name">分类名称，例如 "表情"。</param>
    /// <param name="icon">该分类的代表性 Emoji，例如 "😀"。</param>
    /// <param name="emojis">该分类下的全部条目。</param>
    public EmojiCategory(string name, string icon, IReadOnlyList<EmojiEntry> emojis)
    {
        Name = name;
        Icon = icon;
        Emojis = emojis;
    }

    /// <summary>
    /// 分类名称，例如 "表情"。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 分类的代表性 Emoji，例如 "😀"。
    /// </summary>
    public string Icon { get; }

    /// <summary>
    /// 该分类下的全部 Emoji 条目。
    /// </summary>
    public IReadOnlyList<EmojiEntry> Emojis { get; }
}

/// <summary>
/// 内置 Emoji 数据集：8 个分类以及展平后的全部条目，均在静态构造函数中构建一次。
/// </summary>
public static class EmojiData
{
    private static readonly EmojiCategory[] CategoryArray;
    private static readonly EmojiEntry[] AllArray;

    static EmojiData()
    {
        // --- 表情 ---
        EmojiEntry[] faces =
        {
            new EmojiEntry("😀", "笑脸", "笑脸 高兴 开心 微笑 smile happy grin"),
            new EmojiEntry("😃", "大笑", "大笑 开心 兴奋 张嘴 grin happy big"),
            new EmojiEntry("😄", "眯眼笑", "眯眼笑 开心 喜悦 笑容 smile happy eyes"),
            new EmojiEntry("😁", "露齿笑", "露齿笑 开心 得意 自豪 beam grin happy"),
            new EmojiEntry("😆", "咧嘴笑", "咧嘴笑 大笑 兴奋 眯眼 grin laugh squint"),
            new EmojiEntry("😅", "苦笑", "苦笑 出汗 尴尬 紧张 sweat smile nervous"),
            new EmojiEntry("🤣", "笑翻", "笑翻 大笑 打滚 爆笑 rofl laugh rolling"),
            new EmojiEntry("😂", "笑哭", "笑哭 大笑 眼泪 感动 joy tears laugh"),
            new EmojiEntry("🙂", "微笑", "微笑 轻笑 礼貌 淡定 smile slight"),
            new EmojiEntry("🙃", "倒脸", "倒脸 反讽 无奈 滑稽 upside down irony"),
            new EmojiEntry("🫠", "融化", "融化 尴尬 无奈 消失 melting face awkward"),
            new EmojiEntry("😉", "眨眼", "眨眼 调皮 暗示 俏皮 wink flirt"),
            new EmojiEntry("😊", "害羞笑", "害羞笑 温暖 开心 甜美 blush smile shy"),
            new EmojiEntry("😇", "天使", "天使 纯洁 无辜 善良 angel halo innocent"),
            new EmojiEntry("🥰", "爱心笑", "爱心笑 喜欢 甜蜜 幸福 love hearts smile"),
            new EmojiEntry("😍", "花痴", "花痴 迷恋 喜欢 心动 heart eyes love"),
            new EmojiEntry("🤩", "星星眼", "星星眼 惊艳 崇拜 激动 star struck wow"),
            new EmojiEntry("😘", "飞吻", "飞吻 亲亲 爱你 么么 kiss blow love"),
            new EmojiEntry("☺️", "温和笑", "温和笑 平静 舒服 满足 relaxed smile calm"),
            new EmojiEntry("🥲", "含泪笑", "含泪笑 强颜欢笑 感动 欣慰 smile tear proud"),
            new EmojiEntry("😋", "舔嘴", "舔嘴 好吃 美味 馋 yummy delicious tongue"),
            new EmojiEntry("😛", "吐舌", "吐舌 调皮 玩笑 搞怪 tongue playful"),
            new EmojiEntry("😜", "眨眼吐舌", "眨眼吐舌 调皮 搞怪 得意 wink tongue crazy"),
            new EmojiEntry("🤪", "疯狂", "疯狂 搞怪 滑稽 癫狂 zany crazy wild"),
            new EmojiEntry("😝", "眯眼吐舌", "眯眼吐舌 调皮 鬼脸 得意 squint tongue"),
            new EmojiEntry("🤑", "财迷", "财迷 有钱 暴富 金钱 money mouth rich"),
            new EmojiEntry("🤗", "拥抱", "拥抱 抱抱 欢迎 温暖 hug embrace"),
            new EmojiEntry("🤭", "捂嘴笑", "捂嘴笑 偷笑 害羞 憋笑 giggle hand mouth"),
            new EmojiEntry("🫢", "惊讶捂嘴", "惊讶捂嘴 震惊 尴尬 吃惊 gasp shock hand"),
            new EmojiEntry("🫣", "偷看", "偷看 害羞 尴尬 好奇 peek shy eye"),
            new EmojiEntry("🤫", "嘘", "嘘 安静 保密 小声 quiet shush secret"),
            new EmojiEntry("🤔", "思考", "思考 想 疑惑 考虑 think hmm wonder"),
            new EmojiEntry("🫡", "敬礼", "敬礼 收到 服从 致敬 salute yes sir"),
            new EmojiEntry("🤐", "闭嘴", "闭嘴 不说 保密 沉默 zipper silent mouth"),
            new EmojiEntry("🤨", "挑眉", "挑眉 怀疑 质疑 不解 raised eyebrow doubt"),
            new EmojiEntry("😐", "无语", "无语 平静 冷漠 尴尬 neutral meh blank"),
            new EmojiEntry("😑", "面无表情", "面无表情 无语 无奈 沉默 expressionless blank"),
            new EmojiEntry("😶", "无言", "无言 沉默 尴尬 安静 no mouth silent"),
            new EmojiEntry("🫥", "隐身", "隐身 消失 尴尬 透明 dotted line invisible"),
            new EmojiEntry("😏", "坏笑", "坏笑 得意 暧昧 挑衅 smirk smug"),
            new EmojiEntry("😒", "不爽", "不爽 嫌弃 无奈 不满 unamused annoyed"),
            new EmojiEntry("🙄", "翻白眼", "翻白眼 无语 无奈 嫌弃 roll eyes annoyed"),
            new EmojiEntry("😬", "龇牙", "龇牙 尴尬 紧张 勉强 grimace awkward"),
            new EmojiEntry("😮‍💨", "叹气", "叹气 无奈 疲惫 松口气 exhale sigh relief"),
            new EmojiEntry("🤥", "说谎", "说谎 撒谎 假话 长鼻子 lying pinocchio"),
            new EmojiEntry("😌", "放松", "放松 满足 安心 释然 relieved calm content"),
            new EmojiEntry("😔", "失落", "失落 难过 沉思 抱歉 pensive sad sorry"),
            new EmojiEntry("😴", "睡觉", "睡觉 困 疲惫 休息 sleep tired zzz"),
            new EmojiEntry("🥺", "委屈", "委屈 可怜 请求 撒娇 pleading sad beg"),
            new EmojiEntry("😳", "脸红", "脸红 害羞 尴尬 震惊 flushed blush shy"),
            new EmojiEntry("😤", "傲慢", "傲慢 生气 得意 出气 triumph steam proud"),
            new EmojiEntry("🤯", "爆炸头", "爆炸头 震惊 崩溃 难以置信 mind blown shock"),
            new EmojiEntry("🥳", "派对脸", "派对 庆祝 生日 开心 party celebrate"),
            new EmojiEntry("😎", "墨镜", "墨镜 酷 自信 得意 cool sunglasses"),
            new EmojiEntry("🤓", "书呆子", "书呆子 学霸 眼镜 聪明 nerd geek study"),
            new EmojiEntry("😭", "大哭", "大哭 伤心 眼泪 崩溃 cry sob sad"),
            new EmojiEntry("😱", "惊恐", "惊恐 害怕 尖叫 震惊 scream fear shock"),
            new EmojiEntry("😡", "生气", "生气 愤怒 火大 暴躁 angry mad rage"),
            new EmojiEntry("😈", "恶魔笑", "恶魔 坏笑 调皮 邪恶 devil smile evil"),
            new EmojiEntry("💀", "骷髅", "骷髅 死亡 笑死 头骨 skull dead"),
        };

        // --- 手势 ---
        EmojiEntry[] gestures =
        {
            new EmojiEntry("👋", "挥手", "挥手 你好 再见 打招呼 wave hello bye"),
            new EmojiEntry("🤚", "手背", "手背 举手 停 手掌 back hand stop"),
            new EmojiEntry("🖐️", "张开手", "张开手 五指 手掌 停 hand fingers splayed"),
            new EmojiEntry("✋", "举手", "举手 停下 提问 手掌 raised hand stop"),
            new EmojiEntry("🖖", "瓦肯礼", "瓦肯礼 星际 手势 科幻 vulcan salute spock"),
            new EmojiEntry("🫱", "右手", "右手 向右 手掌 递 rightwards hand"),
            new EmojiEntry("🫲", "左手", "左手 向左 手掌 接 leftwards hand"),
            new EmojiEntry("🫳", "手心向下", "手心向下 放下 招手 撒 palm down hand"),
            new EmojiEntry("🫴", "手心向上", "手心向上 托起 给予 接过 palm up hand"),
            new EmojiEntry("🫷", "左手推", "左手推 推开 拒绝 阻挡 push left hand"),
            new EmojiEntry("🫸", "右手推", "右手推 推开 拒绝 阻挡 push right hand"),
            new EmojiEntry("👌", "OK手势", "OK手势 好的 没问题 同意 ok okay good"),
            new EmojiEntry("🤌", "捏合手", "捏合手 意大利 手势 美味 pinched fingers italian"),
            new EmojiEntry("🤏", "一点点", "一点点 少量 捏 微小 pinch small little"),
            new EmojiEntry("✌️", "剪刀手", "剪刀手 胜利 拍照 耶 victory peace"),
            new EmojiEntry("🤞", "交叉手指", "交叉手指 祈祷 好运 祝福 crossed fingers luck"),
            new EmojiEntry("🫰", "比心", "比心 爱心 手指 喜欢 finger heart love"),
            new EmojiEntry("🤟", "爱你手势", "爱你手势 手语 表白 喜欢 love you gesture"),
            new EmojiEntry("🤘", "摇滚", "摇滚 金属 音乐 手势 rock metal horns"),
            new EmojiEntry("🤙", "打电话", "打电话 联系 呼我 沙卡 call me shaka"),
            new EmojiEntry("👈", "指左", "指左 左边 方向 指向 point left"),
            new EmojiEntry("👉", "指右", "指右 右边 方向 指向 point right"),
            new EmojiEntry("👆", "指上", "指上 上面 方向 指向 point up"),
            new EmojiEntry("🖕", "中指", "中指 冒犯 不雅 愤怒 middle finger"),
            new EmojiEntry("👇", "指下", "指下 下面 方向 指向 point down"),
            new EmojiEntry("☝️", "食指向上", "食指向上 注意 提醒 第一 index up attention"),
            new EmojiEntry("🫵", "指向你", "指向你 你 指向 选中 point you"),
            new EmojiEntry("👍", "赞", "赞 点赞 好 同意 thumbs up good"),
            new EmojiEntry("👎", "差评", "差评 反对 不好 拒绝 thumbs down bad"),
            new EmojiEntry("✊", "握拳", "握拳 加油 力量 团结 raised fist power"),
            new EmojiEntry("👊", "拳头", "拳头 碰拳 加油 出击 fist bump punch"),
            new EmojiEntry("🤛", "左拳", "左拳 碰拳 加油 友谊 left fist bump"),
            new EmojiEntry("🤜", "右拳", "右拳 碰拳 加油 友谊 right fist bump"),
            new EmojiEntry("👏", "鼓掌", "鼓掌 拍手 称赞 欢迎 clap applause"),
            new EmojiEntry("🙌", "举手庆祝", "举手庆祝 万岁 欢呼 成功 raise hands hooray"),
            new EmojiEntry("🫶", "双手比心", "双手比心 爱心 喜欢 感谢 heart hands love"),
            new EmojiEntry("👐", "张开双手", "张开双手 拥抱 欢迎 摊手 open hands"),
            new EmojiEntry("🤲", "双手托", "双手托 请求 捧起 给予 palms up together"),
            new EmojiEntry("🤝", "握手", "握手 合作 达成 你好 handshake deal"),
            new EmojiEntry("🙏", "祈祷", "祈祷 感谢 拜托 合十 pray thanks please"),
            new EmojiEntry("✍️", "写字", "写字 签名 手写 记录 writing hand sign"),
            new EmojiEntry("💅", "美甲", "美甲 指甲 漂亮 打扮 nail polish manicure"),
            new EmojiEntry("🤳", "自拍", "自拍 拍照 手机 合影 selfie phone"),
            new EmojiEntry("💪", "肌肉", "肌肉 强壮 加油 力量 flexed biceps strong"),
            new EmojiEntry("🦾", "机械臂", "机械臂 机器人 力量 义肢 mechanical arm robot"),
        };

        // --- 人物 ---
        EmojiEntry[] people =
        {
            new EmojiEntry("👶", "婴儿", "婴儿 宝宝 可爱 新生 baby infant cute"),
            new EmojiEntry("🧒", "儿童", "儿童 小孩 孩子 child kid"),
            new EmojiEntry("👦", "男孩", "男孩 男生 少年 boy kid"),
            new EmojiEntry("👧", "女孩", "女孩 女生 少女 girl kid"),
            new EmojiEntry("🧑", "成人", "成人 中性 大人 人 person adult"),
            new EmojiEntry("👨", "男人", "男人 男性 男士 man male"),
            new EmojiEntry("👩", "女人", "女人 女性 女士 woman female"),
            new EmojiEntry("🧓", "老人", "老人 长者 年长 银发 older person senior"),
            new EmojiEntry("👴", "老爷爷", "老爷爷 爷爷 男性 长者 old man grandpa"),
            new EmojiEntry("👵", "老奶奶", "老奶奶 奶奶 女性 长者 old woman grandma"),
            new EmojiEntry("🧔", "胡子男", "胡子男 络腮胡 男性 大叔 bearded man"),
            new EmojiEntry("👳", "戴头巾的人", "戴头巾的人 头巾 男性 宗教 turban person"),
            new EmojiEntry("👲", "戴瓜皮帽的人", "戴瓜皮帽的人 帽子 中国 男性 skullcap person"),
            new EmojiEntry("🧕", "戴头巾的女性", "戴头巾的女性 头巾 女性 宗教 headscarf woman"),
            new EmojiEntry("🤵", "新郎", "新郎 西装 婚礼 礼服 tuxedo groom"),
            new EmojiEntry("👰", "新娘", "新娘 婚纱 婚礼 白纱 veil bride"),
            new EmojiEntry("🤰", "孕妇", "孕妇 怀孕 准妈妈 期待 pregnant"),
            new EmojiEntry("🤱", "哺乳", "哺乳 妈妈 喂奶 婴儿 breast feeding"),
            new EmojiEntry("👮", "警察", "警察 警官 执法 公安 police officer cop"),
            new EmojiEntry("🕵️", "侦探", "侦探 侦查 秘密 调查 detective spy"),
            new EmojiEntry("💂", "卫兵", "卫兵 守卫 英国 白金汉 guard british"),
            new EmojiEntry("🥷", "忍者", "忍者 刺客 日本 潜行 ninja assassin"),
            new EmojiEntry("👷", "建筑工人", "建筑工人 工人 施工 安全帽 construction worker"),
            new EmojiEntry("🤴", "王子", "王子 皇室 国王 贵族 prince royal"),
            new EmojiEntry("👸", "公主", "公主 皇室 女王 贵族 princess royal"),
            new EmojiEntry("🧑‍🍳", "厨师", "厨师 做饭 烹饪 厨房 cook chef"),
            new EmojiEntry("🧑‍🎓", "学生", "学生 毕业 学习 学位 student graduate"),
            new EmojiEntry("🧑‍🏫", "老师", "老师 教师 讲课 教学 teacher professor"),
            new EmojiEntry("🧑‍💻", "程序员", "程序员 开发 电脑 技术 technologist developer"),
            new EmojiEntry("🧑‍🔧", "修理工", "修理工 维修 技工 电工 mechanic repair"),
            new EmojiEntry("🧑‍🚀", "宇航员", "宇航员 太空 航天 探索 astronaut space"),
            new EmojiEntry("🧑‍⚖️", "法官", "法官 法院 审判 法律 judge court"),
            new EmojiEntry("🧑‍🌾", "农民", "农民 种地 农业 乡村 farmer agriculture"),
            new EmojiEntry("🧑‍🚒", "消防员", "消防员 灭火 消防 救援 firefighter fire"),
            new EmojiEntry("🧑‍✈️", "飞行员", "飞行员 机长 飞机 航空 pilot captain"),
            new EmojiEntry("🦸", "超级英雄", "超级英雄 英雄 超人 正义 superhero hero"),
            new EmojiEntry("🦹", "超级反派", "超级反派 反派 坏人 邪恶 supervillain villain"),
            new EmojiEntry("🧙", "法师", "法师 魔法 巫师 奇幻 mage wizard magic"),
            new EmojiEntry("🧚", "仙女", "仙女 精灵 魔法 梦幻 fairy magic"),
            new EmojiEntry("🧛", "吸血鬼", "吸血鬼 德古拉 夜晚 恐怖 vampire dracula"),
            new EmojiEntry("🧜", "人鱼", "人鱼 美人鱼 海洋 传说 merperson mermaid"),
            new EmojiEntry("🧝", "精灵", "精灵 奇幻 弓箭 森林 elf fantasy"),
            new EmojiEntry("🧞", "神灯精灵", "神灯精灵 许愿 阿拉丁 魔法 genie wish"),
            new EmojiEntry("🧟", "僵尸", "僵尸 丧尸 恐怖 行走 zombie undead"),
            new EmojiEntry("💆", "按摩", "按摩 放松 头部 舒缓 massage relax"),
            new EmojiEntry("💇", "理发", "理发 剪发 美发 发型 haircut salon"),
            new EmojiEntry("🚶", "走路", "走路 步行 行人 前进 walking pedestrian"),
            new EmojiEntry("🏃", "跑步", "跑步 奔跑 运动 快跑 running jog"),
            new EmojiEntry("💃", "跳舞的女人", "跳舞的女人 舞蹈 拉丁 开心 dancing woman"),
            new EmojiEntry("🕺", "跳舞的男人", "跳舞的男人 舞蹈 迪斯科 开心 dancing man"),
            new EmojiEntry("🧘", "打坐", "打坐 冥想 瑜伽 平静 lotus meditation yoga"),
            new EmojiEntry("🫂", "拥抱的人", "拥抱的人 拥抱 安慰 告别 people hugging"),
            new EmojiEntry("👀", "眼睛", "眼睛 看 关注 偷看 eyes look watch"),
            new EmojiEntry("🧠", "大脑", "大脑 脑子 聪明 思考 brain mind smart"),
            new EmojiEntry("👂", "耳朵", "耳朵 听 倾听 听力 ear listen hear"),
            new EmojiEntry("👃", "鼻子", "鼻子 闻 嗅觉 气味 nose smell"),
            new EmojiEntry("👄", "嘴巴", "嘴巴 嘴 说话 亲吻 mouth lips"),
            new EmojiEntry("🦷", "牙齿", "牙齿 牙 口腔 微笑 tooth dental"),
            new EmojiEntry("👣", "脚印", "脚印 足迹 脚步 踪迹 footprints steps"),
            new EmojiEntry("👤", "人影", "人影 轮廓 用户 匿名 silhouette user"),
        };

        // --- 动物自然 ---
        EmojiEntry[] nature =
        {
            new EmojiEntry("🐶", "小狗", "小狗 狗 宠物 汪 dog puppy"),
            new EmojiEntry("🐱", "小猫", "小猫 猫 宠物 喵 cat kitten"),
            new EmojiEntry("🐭", "老鼠", "老鼠 鼠 小动物 mouse rat"),
            new EmojiEntry("🐹", "仓鼠", "仓鼠 宠物 小动物 hamster"),
            new EmojiEntry("🐰", "兔子", "兔子 兔 可爱 rabbit bunny"),
            new EmojiEntry("🦊", "狐狸", "狐狸 狡猾 野生 fox"),
            new EmojiEntry("🐻", "熊", "熊 棕熊 野生 bear"),
            new EmojiEntry("🐼", "熊猫", "熊猫 国宝 可爱 panda"),
            new EmojiEntry("🐨", "考拉", "考拉 树袋熊 澳洲 koala"),
            new EmojiEntry("🐯", "老虎", "老虎 虎 猛兽 tiger"),
            new EmojiEntry("🦁", "狮子", "狮子 万兽之王 猛兽 lion"),
            new EmojiEntry("🐮", "奶牛", "奶牛 牛 牧场 cow"),
            new EmojiEntry("🐷", "猪", "猪 小猪 农场 pig"),
            new EmojiEntry("🐸", "青蛙", "青蛙 蛙 两栖 frog"),
            new EmojiEntry("🐵", "猴子", "猴子 猴 顽皮 monkey"),
            new EmojiEntry("🙈", "不看猴", "不看 捂眼 害羞 猴子 see no evil monkey"),
            new EmojiEntry("🙉", "不听猴", "不听 捂耳 猴子 hear no evil monkey"),
            new EmojiEntry("🙊", "不说猴", "不说 捂嘴 猴子 speak no evil monkey"),
            new EmojiEntry("🐔", "鸡", "鸡 母鸡 农场 chicken"),
            new EmojiEntry("🐧", "企鹅", "企鹅 南极 可爱 penguin"),
            new EmojiEntry("🐦", "小鸟", "小鸟 鸟 飞行 麻雀 bird"),
            new EmojiEntry("🐤", "小鸡", "小鸡 雏鸟 可爱 chick baby"),
            new EmojiEntry("🦆", "鸭子", "鸭子 鸭 水鸟 duck"),
            new EmojiEntry("🦅", "老鹰", "老鹰 鹰 猛禽 eagle"),
            new EmojiEntry("🦉", "猫头鹰", "猫头鹰 夜行 智慧 owl"),
            new EmojiEntry("🦇", "蝙蝠", "蝙蝠 夜行 飞行 bat"),
            new EmojiEntry("🐺", "狼", "狼 野性 群居 wolf"),
            new EmojiEntry("🐴", "马", "马 骏马 奔跑 horse"),
            new EmojiEntry("🦄", "独角兽", "独角兽 神话 彩虹 unicorn"),
            new EmojiEntry("🐝", "蜜蜂", "蜜蜂 蜂蜜 采蜜 bee honey"),
            new EmojiEntry("🦋", "蝴蝶", "蝴蝶 昆虫 美丽 butterfly"),
            new EmojiEntry("🐌", "蜗牛", "蜗牛 缓慢 外壳 snail"),
            new EmojiEntry("🐞", "瓢虫", "瓢虫 昆虫 幸运 ladybug"),
            new EmojiEntry("🐢", "乌龟", "乌龟 龟 缓慢 turtle"),
            new EmojiEntry("🐍", "蛇", "蛇 爬行 冷血 snake"),
            new EmojiEntry("🐙", "章鱼", "章鱼 海洋 触手 octopus"),
            new EmojiEntry("🐠", "热带鱼", "热带鱼 鱼 海洋 tropical fish"),
            new EmojiEntry("🐟", "鱼", "鱼 鱼类 海鲜 fish"),
            new EmojiEntry("🐬", "海豚", "海豚 海洋 聪明 dolphin"),
            new EmojiEntry("🐳", "鲸鱼", "鲸鱼 海洋 巨大 whale"),
            new EmojiEntry("🦈", "鲨鱼", "鲨鱼 海洋 凶猛 shark"),
            new EmojiEntry("🐘", "大象", "大象 象 庞大 elephant"),
            new EmojiEntry("🦒", "长颈鹿", "长颈鹿 非洲 高大 giraffe"),
            new EmojiEntry("🐾", "爪印", "爪印 脚印 宠物 动物 paw prints"),
            new EmojiEntry("🌵", "仙人掌", "仙人掌 沙漠 植物 cactus"),
            new EmojiEntry("🌲", "松树", "松树 常青 森林 pine tree"),
            new EmojiEntry("🌳", "大树", "大树 树 森林 tree"),
            new EmojiEntry("🌴", "棕榈树", "棕榈树 热带 海滩 palm tree"),
            new EmojiEntry("🌱", "幼苗", "幼苗 发芽 生长 seedling sprout"),
            new EmojiEntry("🍀", "四叶草", "四叶草 幸运 绿色 clover luck"),
            new EmojiEntry("🍄", "蘑菇", "蘑菇 真菌 森林 mushroom"),
            new EmojiEntry("🌹", "玫瑰", "玫瑰 花 浪漫 rose flower"),
            new EmojiEntry("🌸", "樱花", "樱花 花 春天 cherry blossom"),
            new EmojiEntry("🌻", "向日葵", "向日葵 花 阳光 sunflower"),
            new EmojiEntry("⭐", "星星", "星星 星 夜空 star"),
            new EmojiEntry("🔥", "火", "火 火焰 燃烧 fire flame"),
            new EmojiEntry("❄️", "雪花", "雪花 雪 冬天 寒冷 snowflake"),
            new EmojiEntry("🌈", "彩虹", "彩虹 七彩 雨后 rainbow"),
        };

        // --- 食物 ---
        EmojiEntry[] food =
        {
            new EmojiEntry("🍎", "苹果", "苹果 水果 红色 fruit apple"),
            new EmojiEntry("🍐", "梨", "梨 水果 清甜 pear fruit"),
            new EmojiEntry("🍊", "橘子", "橘子 橙子 水果 orange tangerine"),
            new EmojiEntry("🍋", "柠檬", "柠檬 酸 水果 lemon"),
            new EmojiEntry("🍌", "香蕉", "香蕉 水果 黄色 banana"),
            new EmojiEntry("🍉", "西瓜", "西瓜 水果 夏天 watermelon"),
            new EmojiEntry("🍇", "葡萄", "葡萄 水果 紫色 grape"),
            new EmojiEntry("🍓", "草莓", "草莓 水果 甜 strawberry"),
            new EmojiEntry("🫐", "蓝莓", "蓝莓 水果 浆果 blueberry"),
            new EmojiEntry("🍒", "樱桃", "樱桃 水果 红色 cherry"),
            new EmojiEntry("🍑", "桃子", "桃子 水果 甜 peach"),
            new EmojiEntry("🥭", "芒果", "芒果 热带 水果 mango"),
            new EmojiEntry("🍍", "菠萝", "菠萝 热带 水果 pineapple"),
            new EmojiEntry("🥝", "猕猴桃", "猕猴桃 奇异果 水果 kiwi"),
            new EmojiEntry("🍅", "番茄", "番茄 西红柿 蔬菜 tomato"),
            new EmojiEntry("🍆", "茄子", "茄子 蔬菜 紫色 eggplant"),
            new EmojiEntry("🥑", "牛油果", "牛油果 鳄梨 健康 avocado"),
            new EmojiEntry("🥦", "西兰花", "西兰花 蔬菜 健康 broccoli"),
            new EmojiEntry("🥬", "青菜", "青菜 白菜 蔬菜 leafy green"),
            new EmojiEntry("🥒", "黄瓜", "黄瓜 蔬菜 清爽 cucumber"),
            new EmojiEntry("🌶️", "辣椒", "辣椒 辛辣 调味 spicy pepper"),
            new EmojiEntry("🌽", "玉米", "玉米 粗粮 蔬菜 corn"),
            new EmojiEntry("🥕", "胡萝卜", "胡萝卜 蔬菜 橙色 carrot"),
            new EmojiEntry("🧄", "大蒜", "大蒜 蒜 调味 garlic"),
            new EmojiEntry("🧅", "洋葱", "洋葱 调味 蔬菜 onion"),
            new EmojiEntry("🥔", "土豆", "土豆 马铃薯 蔬菜 potato"),
            new EmojiEntry("🥐", "牛角包", "牛角包 面包 早餐 croissant"),
            new EmojiEntry("🍞", "面包", "面包 吐司 早餐 bread toast"),
            new EmojiEntry("🧀", "奶酪", "奶酪 芝士 乳制品 cheese"),
            new EmojiEntry("🥚", "鸡蛋", "鸡蛋 蛋 早餐 egg"),
            new EmojiEntry("🍳", "煎蛋", "煎蛋 早餐 烹饪 fried egg cooking"),
            new EmojiEntry("🥞", "松饼", "松饼 早餐 甜点 pancake"),
            new EmojiEntry("🥓", "培根", "培根 咸肉 早餐 bacon"),
            new EmojiEntry("🥩", "牛排", "牛排 牛肉 西餐 steak beef"),
            new EmojiEntry("🍗", "鸡腿", "鸡腿 鸡肉 炸鸡 chicken leg"),
            new EmojiEntry("🍔", "汉堡", "汉堡 快餐 西餐 burger hamburger"),
            new EmojiEntry("🍟", "薯条", "薯条 快餐 零食 fries"),
            new EmojiEntry("🍕", "披萨", "披萨 芝士 快餐 pizza"),
            new EmojiEntry("🥪", "三明治", "三明治 早餐 快餐 sandwich"),
            new EmojiEntry("🌮", "墨西哥卷", "墨西哥卷 塔可 快餐 taco"),
            new EmojiEntry("🥗", "沙拉", "沙拉 健康 蔬菜 salad"),
            new EmojiEntry("🍜", "拉面", "拉面 面条 汤面 ramen noodles"),
            new EmojiEntry("🍲", "汤", "汤 炖菜 热汤 soup stew"),
            new EmojiEntry("🍣", "寿司", "寿司 日本料理 米饭 sushi"),
            new EmojiEntry("🍦", "冰淇淋", "冰淇淋 甜 冷饮 ice cream"),
            new EmojiEntry("🍰", "蛋糕", "蛋糕 甜点 下午茶 cake dessert"),
            new EmojiEntry("🎂", "生日蛋糕", "生日蛋糕 庆祝 甜点 birthday cake"),
            new EmojiEntry("🍫", "巧克力", "巧克力 甜 零食 chocolate"),
            new EmojiEntry("🍿", "爆米花", "爆米花 电影 零食 popcorn"),
            new EmojiEntry("🍩", "甜甜圈", "甜甜圈 甜点 早餐 donut"),
            new EmojiEntry("🍪", "饼干", "饼干 零食 甜点 cookie biscuit"),
            new EmojiEntry("☕", "咖啡", "咖啡 热饮 提神 coffee"),
            new EmojiEntry("🍵", "茶", "茶 绿茶 热饮 tea"),
            new EmojiEntry("🧋", "奶茶", "奶茶 珍珠 饮品 milk tea boba"),
            new EmojiEntry("🥤", "饮料", "饮料 汽水 冷饮 drink soda"),
            new EmojiEntry("🍺", "啤酒", "啤酒 酒 干杯 beer"),
            new EmojiEntry("🍷", "红酒", "红酒 葡萄酒 干杯 wine"),
            new EmojiEntry("🥂", "香槟", "香槟 庆祝 干杯 champagne cheers"),
        };

        // --- 活动 ---
        EmojiEntry[] activities =
        {
            new EmojiEntry("⚽", "足球", "足球 运动 球类 soccer football"),
            new EmojiEntry("🏀", "篮球", "篮球 运动 球类 basketball"),
            new EmojiEntry("🏈", "橄榄球", "橄榄球 运动 球类 rugby football"),
            new EmojiEntry("⚾", "棒球", "棒球 运动 球类 baseball"),
            new EmojiEntry("🎾", "网球", "网球 运动 球类 tennis"),
            new EmojiEntry("🏐", "排球", "排球 运动 球类 volleyball"),
            new EmojiEntry("🎱", "台球", "台球 桌球 运动 billiards pool"),
            new EmojiEntry("🏓", "乒乓球", "乒乓球 球拍 运动 ping pong"),
            new EmojiEntry("🏸", "羽毛球", "羽毛球 球拍 运动 badminton"),
            new EmojiEntry("🏒", "冰球", "冰球 冬季 运动 ice hockey"),
            new EmojiEntry("🏑", "曲棍球", "曲棍球 球杆 运动 field hockey"),
            new EmojiEntry("🥅", "球门", "球门 进球 运动 goal net"),
            new EmojiEntry("⛳", "高尔夫", "高尔夫 球洞 运动 golf"),
            new EmojiEntry("🏹", "射箭", "射箭 弓箭 运动 archery bow"),
            new EmojiEntry("🎣", "钓鱼", "钓鱼 鱼竿 休闲 fishing"),
            new EmojiEntry("🤿", "潜水", "潜水 浮潜 海洋 diving snorkel"),
            new EmojiEntry("🥊", "拳击", "拳击 手套 格斗 boxing"),
            new EmojiEntry("🥋", "武术", "武术 道服 格斗 martial arts karate"),
            new EmojiEntry("🎽", "运动服", "运动服 背心 跑步 running shirt"),
            new EmojiEntry("🛹", "滑板", "滑板 街头 运动 skateboard"),
            new EmojiEntry("🛼", "轮滑", "轮滑 溜冰鞋 运动 roller skate"),
            new EmojiEntry("🛷", "雪橇", "雪橇 冬季 雪地 sled"),
            new EmojiEntry("⛸️", "溜冰", "溜冰 冰鞋 冬季 ice skate"),
            new EmojiEntry("🎿", "滑雪", "滑雪 雪板 冬季 ski"),
            new EmojiEntry("🏂", "单板滑雪", "单板滑雪 雪板 冬季 snowboard"),
            new EmojiEntry("🪂", "跳伞", "跳伞 降落伞 极限 parachute skydive"),
            new EmojiEntry("🏋️", "举重", "举重 杠铃 健身 weight lifting"),
            new EmojiEntry("🤸", "体操", "体操 翻跟头 运动 gymnastics"),
            new EmojiEntry("🤺", "击剑", "击剑 剑术 运动 fencing"),
            new EmojiEntry("🏇", "赛马", "赛马 骑马 运动 horse racing"),
            new EmojiEntry("🏄", "冲浪", "冲浪 海浪 运动 surfing"),
            new EmojiEntry("🏊", "游泳", "游泳 泳池 运动 swimming"),
            new EmojiEntry("🚴", "骑行", "骑行 自行车 运动 cycling"),
            new EmojiEntry("🧗", "攀岩", "攀岩 登山 极限 climbing"),
            new EmojiEntry("🏆", "奖杯", "奖杯 冠军 胜利 trophy champion"),
            new EmojiEntry("🥇", "金牌", "金牌 第一 冠军 gold medal first"),
            new EmojiEntry("🥈", "银牌", "银牌 第二 亚军 silver medal"),
            new EmojiEntry("🥉", "铜牌", "铜牌 第三 季军 bronze medal"),
            new EmojiEntry("🏅", "奖牌", "奖牌 荣誉 运动 medal sports"),
            new EmojiEntry("🎖️", "勋章", "勋章 荣誉 奖励 military medal"),
            new EmojiEntry("🎫", "门票", "门票 入场券 演出 ticket"),
            new EmojiEntry("🎪", "马戏团", "马戏团 帐篷 表演 circus"),
            new EmojiEntry("🤹", "杂耍", "杂耍 抛球 表演 juggling"),
            new EmojiEntry("🎭", "戏剧", "戏剧 面具 表演 theater drama"),
            new EmojiEntry("🎨", "调色板", "调色板 画画 艺术 palette art"),
            new EmojiEntry("🎬", "电影", "电影 拍摄 场记 movie film"),
            new EmojiEntry("🎤", "麦克风", "麦克风 唱歌 演讲 microphone sing"),
            new EmojiEntry("🎧", "耳机", "耳机 音乐 听歌 headphones music"),
            new EmojiEntry("🎼", "乐谱", "乐谱 音乐 谱子 sheet music score"),
            new EmojiEntry("🎹", "钢琴", "钢琴 键盘 音乐 piano keyboard"),
        };

        // --- 旅行 ---
        EmojiEntry[] travel =
        {
            new EmojiEntry("🚗", "汽车", "汽车 轿车 出行 car"),
            new EmojiEntry("🚕", "出租车", "出租车 打车 出行 taxi"),
            new EmojiEntry("🚙", "越野车", "越野车 吉普 出行 suv jeep"),
            new EmojiEntry("🚌", "公交车", "公交车 巴士 出行 bus"),
            new EmojiEntry("🚎", "无轨电车", "无轨电车 电车 出行 trolleybus"),
            new EmojiEntry("🏎️", "赛车", "赛车 竞速 跑车 race car"),
            new EmojiEntry("🚓", "警车", "警车 警察 出警 police car"),
            new EmojiEntry("🚑", "救护车", "救护车 急救 医疗 ambulance"),
            new EmojiEntry("🚒", "消防车", "消防车 灭火 救援 fire engine"),
            new EmojiEntry("🚐", "面包车", "面包车 商务车 出行 minibus van"),
            new EmojiEntry("🛻", "皮卡", "皮卡 货车 出行 pickup truck"),
            new EmojiEntry("🚚", "货车", "货车 运输 物流 truck"),
            new EmojiEntry("🚛", "卡车", "卡车 货运 重型 lorry"),
            new EmojiEntry("🚜", "拖拉机", "拖拉机 农场 农业 tractor"),
            new EmojiEntry("🛵", "踏板车", "踏板车 摩托 出行 scooter"),
            new EmojiEntry("🏍️", "摩托车", "摩托车 机车 骑行 motorcycle"),
            new EmojiEntry("🛺", "三轮车", "三轮车 嘟嘟车 出行 rickshaw"),
            new EmojiEntry("🚲", "自行车", "自行车 单车 骑行 bicycle"),
            new EmojiEntry("🛴", "滑板车", "滑板车 电动 出行 scooter kick"),
            new EmojiEntry("🚨", "警灯", "警灯 警报 闪烁 police light siren"),
            new EmojiEntry("🚄", "高铁", "高铁 动车 列车 bullet train"),
            new EmojiEntry("🚂", "蒸汽火车", "蒸汽火车 火车头 铁路 steam train"),
            new EmojiEntry("🚆", "火车", "火车 列车 铁路 train"),
            new EmojiEntry("🚇", "地铁", "地铁 地下 通勤 subway metro"),
            new EmojiEntry("🚊", "有轨电车", "有轨电车 电车 城市 tram"),
            new EmojiEntry("🚉", "车站", "车站 站台 候车 station platform"),
            new EmojiEntry("🚡", "缆车", "缆车 索道 上山 cable car"),
            new EmojiEntry("✈️", "飞机", "飞机 航班 出行 airplane flight"),
            new EmojiEntry("🛫", "起飞", "起飞 出发 航班 takeoff departure"),
            new EmojiEntry("🛬", "降落", "降落 到达 航班 landing arrival"),
            new EmojiEntry("🛩️", "小飞机", "小飞机 私人飞机 航空 small plane"),
            new EmojiEntry("💺", "座位", "座位 座椅 机票 seat"),
            new EmojiEntry("🛰️", "卫星", "卫星 太空 通信 satellite"),
            new EmojiEntry("🚀", "火箭", "火箭 发射 太空 rocket launch"),
            new EmojiEntry("🛸", "飞碟", "飞碟 外星 不明飞行物 ufo alien"),
            new EmojiEntry("🚁", "直升机", "直升机 救援 飞行 helicopter"),
            new EmojiEntry("⛵", "帆船", "帆船 出海 航行 sailboat"),
            new EmojiEntry("🚤", "快艇", "快艇 摩托艇 出海 speedboat"),
            new EmojiEntry("🛳️", "游轮", "游轮 邮轮 度假 cruise ship"),
            new EmojiEntry("⚓", "锚", "锚 港口 航海 anchor port"),
            new EmojiEntry("⛽", "加油站", "加油站 加油 汽油 gas station"),
            new EmojiEntry("🚧", "施工", "施工 路障 修路 construction"),
            new EmojiEntry("🚦", "红绿灯", "红绿灯 交通 信号 traffic light"),
            new EmojiEntry("🗺️", "地图", "地图 导航 路线 map"),
            new EmojiEntry("🗽", "自由女神", "自由女神 纽约 地标 statue liberty"),
            new EmojiEntry("🗼", "东京塔", "东京塔 日本 地标 tokyo tower"),
            new EmojiEntry("🏰", "城堡", "城堡 欧洲 童话 castle"),
            new EmojiEntry("🏯", "日本城堡", "日本城堡 天守阁 日本 japanese castle"),
            new EmojiEntry("🎡", "摩天轮", "摩天轮 游乐园 浪漫 ferris wheel"),
            new EmojiEntry("🎢", "过山车", "过山车 游乐园 刺激 roller coaster"),
        };

        // --- 符号 ---
        EmojiEntry[] symbols =
        {
            new EmojiEntry("❤️", "红心", "红心 爱 喜欢 红色 red heart love"),
            new EmojiEntry("🧡", "橙心", "橙心 橙色 温暖 orange heart"),
            new EmojiEntry("💛", "黄心", "黄心 黄色 友谊 yellow heart"),
            new EmojiEntry("💚", "绿心", "绿心 绿色 环保 green heart"),
            new EmojiEntry("💙", "蓝心", "蓝心 蓝色 平静 blue heart"),
            new EmojiEntry("💜", "紫心", "紫心 紫色 温柔 purple heart"),
            new EmojiEntry("🖤", "黑心", "黑心 黑色 酷 black heart"),
            new EmojiEntry("🤍", "白心", "白心 白色 纯洁 white heart"),
            new EmojiEntry("🤎", "棕心", "棕心 棕色 温暖 brown heart"),
            new EmojiEntry("💔", "心碎", "心碎 分手 难过 broken heart"),
            new EmojiEntry("❤️‍🔥", "燃烧的心", "燃烧的心 热爱 激情 heart on fire"),
            new EmojiEntry("❤️‍🩹", "疗愈的心", "疗愈的心 修复 治愈 mending heart"),
            new EmojiEntry("💕", "两颗心", "两颗心 恋爱 甜蜜 two hearts"),
            new EmojiEntry("💞", "旋转的心", "旋转的心 恋爱 循环 revolving hearts"),
            new EmojiEntry("💓", "跳动的心", "跳动的心 心跳 喜欢 beating heart"),
            new EmojiEntry("💗", "长大的心", "长大的心 喜欢 成长 growing heart"),
            new EmojiEntry("💖", "闪亮的心", "闪亮的心 喜欢 闪耀 sparkling heart"),
            new EmojiEntry("💘", "一箭穿心", "一箭穿心 恋爱 丘比特 cupid arrow heart"),
            new EmojiEntry("💝", "礼物心", "礼物心 礼物 节日 gift heart"),
            new EmojiEntry("💟", "心形装饰", "心形装饰 爱心 图案 heart decoration"),
            new EmojiEntry("☮️", "和平", "和平 反战 标志 peace symbol"),
            new EmojiEntry("✝️", "十字架", "十字架 基督教 宗教 cross christian"),
            new EmojiEntry("☪️", "星月", "星月 伊斯兰 宗教 crescent islam"),
            new EmojiEntry("🕉️", "唵", "唵 印度教 宗教 om hindu"),
            new EmojiEntry("☸️", "法轮", "法轮 佛教 宗教 dharma wheel"),
            new EmojiEntry("✡️", "六芒星", "六芒星 犹太教 宗教 star david"),
            new EmojiEntry("☯️", "阴阳", "阴阳 太极 平衡 yin yang"),
            new EmojiEntry("⚛️", "原子", "原子 科学 物理 atom science"),
            new EmojiEntry("☢️", "辐射", "辐射 危险 核 radioactive nuclear"),
            new EmojiEntry("⚠️", "警告", "警告 注意 小心 warning caution"),
            new EmojiEntry("⛔", "禁止进入", "禁止进入 禁止 交通 no entry"),
            new EmojiEntry("🚫", "禁止", "禁止 不允许 阻止 prohibited"),
            new EmojiEntry("♈", "白羊座", "白羊座 星座 火象 aries"),
            new EmojiEntry("♉", "金牛座", "金牛座 星座 土象 taurus"),
            new EmojiEntry("♊", "双子座", "双子座 星座 风象 gemini"),
            new EmojiEntry("♋", "巨蟹座", "巨蟹座 星座 水象 cancer"),
            new EmojiEntry("♌", "狮子座", "狮子座 星座 火象 leo"),
            new EmojiEntry("♍", "处女座", "处女座 星座 土象 virgo"),
            new EmojiEntry("♎", "天秤座", "天秤座 星座 风象 libra"),
            new EmojiEntry("♏", "天蝎座", "天蝎座 星座 水象 scorpio"),
            new EmojiEntry("♐", "射手座", "射手座 星座 火象 sagittarius"),
            new EmojiEntry("♑", "摩羯座", "摩羯座 星座 土象 capricorn"),
            new EmojiEntry("♒", "水瓶座", "水瓶座 星座 风象 aquarius"),
            new EmojiEntry("♓", "双鱼座", "双鱼座 星座 水象 pisces"),
            new EmojiEntry("🔴", "红圆", "红色 圆形 圆点 red circle"),
            new EmojiEntry("🟠", "橙圆", "橙色 圆形 圆点 orange circle"),
            new EmojiEntry("🟡", "黄圆", "黄色 圆形 圆点 yellow circle"),
            new EmojiEntry("🟢", "绿圆", "绿色 圆形 圆点 green circle"),
            new EmojiEntry("🔵", "蓝圆", "蓝色 圆形 圆点 blue circle"),
            new EmojiEntry("🟣", "紫圆", "紫色 圆形 圆点 purple circle"),
            new EmojiEntry("⚫", "黑圆", "黑色 圆形 圆点 black circle"),
            new EmojiEntry("⚪", "白圆", "白色 圆形 圆点 white circle"),
            new EmojiEntry("❌", "叉号", "叉号 错误 取消 cross mark"),
            new EmojiEntry("💯", "满分", "满分 一百 赞同 hundred points"),
            new EmojiEntry("♻️", "回收", "回收 环保 循环 recycle"),
            new EmojiEntry("🎵", "音符", "音符 音乐 旋律 musical note"),
            new EmojiEntry("🎶", "音符们", "音符们 音乐 旋律 musical notes"),
            new EmojiEntry("♾️", "无穷", "无穷 无限 数学 infinity"),
        };

        CategoryArray = new[]
        {
            new EmojiCategory("表情", "😀", Array.AsReadOnly(faces)),
            new EmojiCategory("手势", "👍", Array.AsReadOnly(gestures)),
            new EmojiCategory("人物", "🧑", Array.AsReadOnly(people)),
            new EmojiCategory("动物自然", "🐱", Array.AsReadOnly(nature)),
            new EmojiCategory("食物", "🍎", Array.AsReadOnly(food)),
            new EmojiCategory("活动", "⚽", Array.AsReadOnly(activities)),
            new EmojiCategory("旅行", "✈️", Array.AsReadOnly(travel)),
            new EmojiCategory("符号", "❤️", Array.AsReadOnly(symbols)),
        };

        List<EmojiEntry> all = new List<EmojiEntry>(512);
        for (int i = 0; i < CategoryArray.Length; i++)
        {
            all.AddRange(CategoryArray[i].Emojis);
        }

        AllArray = all.ToArray();
        Categories = Array.AsReadOnly(CategoryArray);
        All = Array.AsReadOnly(AllArray);
    }

    /// <summary>
    /// 全部 Emoji 分类，顺序为：表情、手势、人物、动物自然、食物、活动、旅行、符号。
    /// </summary>
    public static IReadOnlyList<EmojiCategory> Categories { get; }

    /// <summary>
    /// 展平后的全部 Emoji 条目（按分类顺序拼接），仅构建一次。
    /// </summary>
    public static IReadOnlyList<EmojiEntry> All { get; }
}
