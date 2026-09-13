using System.Security.Cryptography;
using System.Text;

namespace Wolfstare.Core.Sessions;

/// <summary>
/// Generates the random text a <see cref="RandomTextLock"/> makes the user retype.
///
/// Random common words, space-separated, rather than character soup: far easier for a human to
/// read from the challenge and type, while still long enough to be a real deterrent. This is
/// friction, not cryptography — the point is the effort of typing it, not that it is secret.
/// </summary>
public static class RandomText
{
    // Short, common, unambiguous lowercase words — quick to read and type, no look-alikes.
    private static readonly string[] Words =
    [
        "able", "acid", "aged", "also", "area", "army", "away", "baby", "back", "ball",
        "band", "bank", "base", "bath", "bear", "beat", "been", "beer", "bell", "best",
        "bird", "blue", "boat", "body", "bone", "book", "born", "both", "bowl", "bulk",
        "burn", "bush", "busy", "cake", "call", "calm", "came", "camp", "card", "care",
        "case", "cash", "cast", "cell", "chat", "chip", "city", "club", "coal", "coat",
        "code", "cold", "come", "cook", "cool", "cope", "copy", "core", "corn", "cost",
        "crew", "crop", "dark", "data", "date", "dawn", "days", "dead", "deal", "dear",
        "debt", "deep", "deny", "desk", "dial", "diet", "dirt", "dish", "does", "done",
        "door", "dose", "down", "draw", "drop", "drug", "dual", "duke", "dust", "duty",
        "each", "earn", "ease", "east", "easy", "edge", "else", "even", "ever", "evil",
        "face", "fact", "fade", "fail", "fair", "fall", "farm", "fast", "fate", "fear",
        "feed", "feel", "feet", "fell", "file", "fill", "film", "find", "fine", "fire",
        "firm", "fish", "five", "flat", "flow", "food", "foot", "ford", "form", "fort",
        "four", "free", "from", "fuel", "full", "fund", "gain", "game", "gate", "gave",
        "gear", "gene", "gift", "girl", "give", "glad", "goal", "goes", "gold", "golf",
        "gone", "good", "gray", "grew", "grid", "grow", "gulf", "hair", "half", "hall",
        "hand", "hang", "hard", "harm", "hate", "have", "head", "hear", "heat", "held",
        "hell", "help", "here", "hero", "high", "hill", "hint", "hire", "hold", "hole",
        "holy", "home", "hope", "host", "hour", "huge", "hung", "hunt", "hurt", "idea",
        "inch", "into", "iron", "item", "join", "jump", "jury", "just", "keen", "keep",
        "kept", "kick", "kind", "king", "knee", "knew", "know", "lack", "lady", "lake",
        "land", "lane", "last", "late", "lead", "leaf", "lean", "left", "less", "life",
        "lift", "like", "line", "link", "list", "live", "load", "loan", "lock", "logo",
        "long", "look", "lord", "lose", "loss", "lost", "loud", "love", "luck", "made",
        "mail", "main", "make", "male", "mall", "many", "mark", "mass", "math", "meal",
        "mean", "meat", "meet", "menu", "mere", "mice", "mild", "mile", "milk", "mind",
        "mine", "miss", "mode", "mood", "moon", "more", "most", "move", "much", "must",
        "name", "navy", "near", "neat", "neck", "need", "news", "next", "nice", "nine",
        "node", "none", "noon", "norm", "nose", "note", "obey", "okay", "once", "only",
        "onto", "open", "oral", "over", "pace", "pack", "page", "paid", "pain", "pair",
        "palm", "park", "part", "pass", "past", "path", "peak", "pick", "pile", "pill",
        "pine", "pink", "pipe", "plan", "play", "plot", "plus", "poem", "poet", "pole",
        "poll", "pool", "poor", "port", "pose", "post", "pour", "pray", "prep", "prey",
        "pull", "pure", "push", "quit", "race", "rail", "rain", "rank", "rare", "rate",
        "read", "real", "rear", "rely", "rent", "rest", "rice", "rich", "ride", "ring",
        "rise", "risk", "road", "rock", "role", "roll", "roof", "room", "root", "rope",
        "rose", "rule", "rush", "safe", "said", "sail", "sake", "sale", "salt", "same",
        "sand", "save", "seat", "seed", "seek", "seem", "seen", "self", "sell", "send",
        "sept", "ship", "shop", "shot", "show", "shut", "sick", "side", "sign", "silk",
        "site", "size", "skin", "slip", "slow", "snap", "snow", "soft", "soil", "sold",
        "sole", "some", "song", "soon", "sort", "soul", "soup", "spot", "star", "stay",
        "stem", "step", "stir", "stop", "such", "suit", "sure", "swim", "tail", "take",
        "tale", "talk", "tall", "tank", "tape", "task", "team", "tear", "tell", "tend",
        "term", "test", "text", "than", "that", "them", "then", "they", "thin", "this",
        "thus", "tide", "tidy", "tied", "tile", "time", "tiny", "toll", "tone", "took",
        "tool", "torn", "tour", "town", "trap", "tree", "trip", "true", "tube", "tune",
        "turn", "twin", "type", "unit", "upon", "urge", "used", "user", "vary", "vast",
        "very", "vice", "view", "vote", "wage", "wait", "wake", "walk", "wall", "want",
        "ward", "warm", "wash", "wave", "ways", "weak", "wear", "week", "well", "went",
        "were", "west", "what", "when", "whom", "wide", "wife", "wild", "will", "wind",
        "wine", "wing", "wipe", "wire", "wise", "wish", "with", "wood", "word", "wore",
        "work", "yard", "yarn", "year", "your", "zero", "zone", "zoom",
    ];

    /// <summary>
    /// Produces space-separated random words up to <paramref name="targetLength"/> characters.
    /// The result is at most that length (it stops before overshooting), and always at least one
    /// word.
    /// </summary>
    public static string Generate(int targetLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetLength, 1);

        var builder = new StringBuilder(targetLength);
        while (true)
        {
            var word = Words[RandomNumberGenerator.GetInt32(Words.Length)];
            var addition = builder.Length == 0 ? word.Length : word.Length + 1; // + separating space
            if (builder.Length + addition > targetLength) break;

            if (builder.Length > 0) builder.Append(' ');
            builder.Append(word);
        }

        // Guarantee a non-empty result even for a tiny target (the API floor is 200, so this only
        // matters to defensive callers).
        if (builder.Length == 0)
            builder.Append(Words[RandomNumberGenerator.GetInt32(Words.Length)]);

        return builder.ToString();
    }
}
