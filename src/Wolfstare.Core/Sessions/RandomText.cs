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
    // Everyday English words a student knows well before the end of high school — nothing
    // obscure, technical, or archaic, and all lowercase with no look-alike-free constraint
    // (words are easy to read even with an o or an l). Mixed lengths up to ten letters so the
    // text reads naturally. Kept clean and neutral.
    private static readonly string[] Words =
    [
        "about", "above", "across", "act", "add", "afraid", "after", "again", "age", "agree",
        "air", "allow", "almost", "alone", "along", "already", "also", "always", "among", "anger",
        "animal", "answer", "any", "apple", "area", "argue", "arm", "army", "around", "arrive",
        "art", "ask", "attack", "aunt", "autumn", "away", "baby", "back", "bad", "bag",
        "bake", "ball", "band", "bank", "base", "basket", "bath", "beach", "bean", "bear",
        "beat", "become", "bed", "before", "begin", "behind", "being", "believe", "bell", "belong",
        "below", "belt", "bend", "best", "better", "between", "big", "bird", "birth", "bit",
        "bite", "black", "blame", "blank", "blind", "block", "blood", "blow", "blue", "board",
        "boat", "body", "bone", "book", "boot", "border", "bored", "born", "borrow", "boss",
        "both", "bottle", "bottom", "bowl", "box", "boy", "brain", "branch", "brave", "bread",
        "break", "bright", "bring", "broad", "broken", "brother", "brown", "brush", "build", "burn",
        "bury", "bus", "bush", "busy", "butter", "button", "buy", "cage", "cake", "call",
        "calm", "camera", "camp", "can", "candle", "candy", "cap", "car", "card", "care",
        "careful", "carry", "case", "cash", "castle", "cat", "catch", "cause", "cave", "cent",
        "center", "chair", "chance", "change", "cheap", "check", "cheese", "chest", "child", "choice",
        "choose", "circle", "city", "class", "clean", "clear", "clever", "climb", "clock", "close",
        "cloth", "cloud", "club", "coach", "coast", "coat", "coffee", "coin", "cold", "collect",
        "color", "come", "common", "cook", "cool", "copy", "corner", "cost", "cotton", "count",
        "country", "couple", "course", "cover", "cow", "crash", "crazy", "cream", "create", "crime",
        "cross", "crowd", "cry", "cup", "cut", "dance", "danger", "dark", "date", "day",
        "dead", "deal", "dear", "death", "decide", "deep", "degree", "deliver", "depend", "desk",
        "detail", "die", "diet", "differ", "dinner", "dirty", "doctor", "dog", "dollar", "door",
        "double", "down", "dozen", "draw", "dream", "dress", "drink", "drive", "drop", "drum",
        "dry", "duck", "during", "dust", "duty", "each", "ear", "early", "earn", "earth",
        "east", "easy", "eat", "edge", "effort", "egg", "eight", "either", "empty", "end",
        "enemy", "enjoy", "enough", "enter", "equal", "escape", "even", "event", "ever", "every",
        "exact", "example", "expect", "explain", "extra", "eye", "face", "fact", "fail", "fair",
        "fall", "family", "famous", "far", "farm", "fast", "fat", "father", "fault", "fear",
        "feed", "feel", "few", "field", "fight", "fill", "film", "final", "find", "fine",
        "finger", "finish", "fire", "first", "fish", "fit", "five", "fix", "flag", "flat",
        "float", "floor", "flower", "fly", "focus", "fold", "follow", "food", "fool", "foot",
        "force", "forest", "forget", "fork", "form", "forward", "four", "free", "fresh", "friend",
        "front", "fruit", "full", "fun", "funny", "future", "game", "garden", "gas", "gate",
        "gather", "gentle", "gift", "girl", "give", "glad", "glass", "goal", "goat", "gold",
        "good", "grade", "grain", "grand", "grass", "great", "green", "grey", "ground", "group",
        "grow", "guard", "guess", "guest", "guide", "gun", "hair", "half", "hall", "hand",
        "hang", "happen", "happy", "hard", "hat", "hate", "head", "health", "hear", "heart",
        "heat", "heavy", "hello", "help", "here", "hero", "hide", "high", "hill", "history",
        "hit", "hobby", "hold", "hole", "home", "honest", "honey", "hope", "horse", "hospital",
        "hot", "hotel", "hour", "house", "how", "huge", "human", "hundred", "hungry", "hunt",
        "hurry", "hurt", "ice", "idea", "ill", "image", "important", "improve", "inch", "include",
        "income", "indeed", "inside", "invite", "iron", "island", "job", "join", "joke", "joy",
        "judge", "juice", "jump", "just", "keep", "key", "kick", "kid", "kill", "kind",
        "king", "kiss", "kitchen", "knee", "knife", "knock", "know", "lake", "lamp", "land",
        "large", "last", "late", "later", "laugh", "law", "lay", "lazy", "lead", "leaf",
        "learn", "least", "leave", "left", "leg", "lemon", "lend", "length", "less", "lesson",
        "letter", "level", "library", "lie", "life", "lift", "light", "like", "line", "lion",
        "lip", "list", "listen", "little", "live", "load", "local", "lock", "lonely", "long",
        "look", "lose", "loss", "lost", "lot", "loud", "love", "low", "luck", "lunch",
        "machine", "mad", "mail", "main", "major", "make", "man", "manage", "many", "map",
        "mark", "market", "marry", "mass", "master", "match", "matter", "may", "maybe", "meal",
        "mean", "meat", "meet", "member", "memory", "mention", "menu", "mess", "message", "metal",
        "middle", "might", "mile", "milk", "mind", "mine", "minute", "mirror", "miss", "mistake",
        "mix", "model", "modern", "moment", "money", "monkey", "month", "moon", "more", "morning",
        "most", "mother", "mountain", "mouse", "mouth", "move", "movie", "much", "music", "must",
        "nail", "name", "narrow", "nation", "nature", "near", "neck", "need", "needle", "nephew",
        "nervous", "nest", "net", "never", "new", "news", "next", "nice", "night", "nine",
        "noise", "none", "noon", "north", "nose", "note", "nothing", "notice", "now", "number",
        "nurse", "nut", "obey", "object", "ocean", "offer", "office", "often", "oil", "old",
        "once", "onion", "only", "open", "orange", "order", "other", "out", "oven", "over",
        "owe", "own", "page", "pain", "paint", "pair", "palace", "pale", "pan", "paper",
        "parent", "park", "part", "party", "pass", "past", "path", "pay", "peace", "pen",
        "pencil", "people", "pepper", "perfect", "perhaps", "person", "phone", "photo", "piano", "pick",
        "picture", "piece", "pig", "pile", "pilot", "pink", "pipe", "place", "plain", "plan",
        "plane", "plant", "plate", "play", "please", "plenty", "pocket", "point", "poison", "police",
        "polite", "pool", "poor", "pop", "popular", "pot", "potato", "pound", "power", "pray",
        "prefer", "prepare", "present", "press", "pretty", "price", "pride", "prince", "print", "prison",
        "prize", "problem", "produce", "promise", "proud", "prove", "public", "pull", "pump", "punish",
        "pupil", "pure", "purple", "push", "put", "queen", "question", "quick", "quiet", "quite",
        "rabbit", "race", "radio", "rain", "raise", "rather", "reach", "read", "ready", "real",
        "reason", "record", "red", "remember", "remind", "remove", "rent", "repair", "repeat", "reply",
        "report", "rescue", "rest", "return", "rice", "rich", "ride", "right", "ring", "rise",
        "river", "road", "rock", "role", "roll", "roof", "room", "root", "rope", "rose",
        "rough", "round", "row", "rubber", "rude", "rule", "run", "rush", "sad", "safe",
        "sail", "salt", "same", "sand", "save", "say", "scale", "scared", "school", "science",
        "scissors", "score", "sea", "search", "season", "seat", "second", "secret", "see", "seed",
        "seem", "sell", "send", "sense", "sentence", "serve", "settle", "seven", "several", "sew",
        "shadow", "shake", "shall", "shame", "shape", "share", "sharp", "she", "sheep", "sheet",
        "shelf", "shell", "shine", "ship", "shirt", "shock", "shoe", "shoot", "shop", "short",
        "should", "shoulder", "shout", "show", "shut", "shy", "sick", "side", "sight", "sign",
        "silent", "silk", "silly", "silver", "simple", "since", "sing", "single", "sink", "sister",
        "sit", "six", "size", "skill", "skin", "skirt", "sky", "sleep", "slice", "slow",
        "small", "smart", "smell", "smile", "smoke", "snake", "snow", "soap", "sock", "soft",
        "soil", "soldier", "some", "son", "song", "soon", "sorry", "sort", "sound", "soup",
        "south", "space", "speak", "special", "speed", "spell", "spend", "spider", "spoon", "sport",
        "spread", "spring", "square", "stage", "stairs", "stamp", "stand", "star", "start", "state",
        "station", "stay", "steal", "steam", "steel", "step", "stick", "still", "stone", "stop",
        "store", "storm", "story", "straight", "strange", "street", "strong", "student", "study", "stupid",
        "such", "sudden", "sugar", "suit", "summer", "sun", "supper", "sure", "surprise", "sweet",
        "swim", "table", "tail", "take", "talk", "tall", "taste", "taxi", "tea", "teach",
        "team", "tear", "teeth", "telephone", "tell", "temple", "ten", "tennis", "tent", "terrible",
        "test", "than", "thank", "that", "theater", "their", "them", "then", "there", "these",
        "they", "thick", "thin", "thing", "think", "third", "thirsty", "this", "though", "thought",
        "three", "throat", "throw", "thumb", "ticket", "tidy", "tie", "tiger", "time", "tiny",
        "tired", "title", "today", "toe", "together", "tomato", "tomorrow", "tongue", "tonight", "too",
        "tool", "tooth", "top", "total", "touch", "tough", "toward", "towel", "tower", "town",
        "toy", "trade", "traffic", "train", "travel", "tree", "trip", "trouble", "truck", "true",
        "trust", "truth", "try", "tube", "turn", "twelve", "twenty", "twice", "twin", "two",
        "ugly", "umbrella", "uncle", "under", "understand", "unit", "until", "upon", "upset", "use",
        "useful", "usual", "valley", "value", "van", "vegetable", "very", "video", "view", "village",
        "visit", "voice", "wait", "wake", "walk", "wall", "want", "war", "warm", "wash",
        "waste", "watch", "water", "wave", "way", "weak", "wealth", "wear", "weather", "wedding",
        "week", "weight", "welcome", "well", "west", "wet", "what", "wheel", "when", "where",
        "which", "while", "white", "who", "whole", "why", "wide", "wife", "wild", "will",
        "win", "wind", "window", "wine", "wing", "winter", "wire", "wise", "wish", "with",
        "woman", "wonder", "wood", "wool", "word", "work", "world", "worry", "worse", "worth",
        "would", "wound", "wrap", "write", "wrong", "yard", "year", "yellow", "yes", "young",
        "youth", "zero", "zone", "zoo",
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
