using System.Text.RegularExpressions;

namespace WhisperTyper
{
    public class FillerWordFilter
    {
        public static readonly string[] Defaults =
        [
            "um", "uh", "er", "ah", "like", "you know", "i mean",
            "sort of", "kind of", "basically", "literally", "actually",
            "so", "well", "right", "okay", "yeah"
        ];

        private List<Regex> _patterns = [];
        private static readonly Regex _multiSpace    = new(@" {2,}",        RegexOptions.Compiled);
        private static readonly Regex _leadingPunct  = new(@"^[\s,\.]+",    RegexOptions.Compiled);
        private static readonly Regex _doublePunct   = new(@"([,\.]) (?=[,\.])", RegexOptions.Compiled);

        public bool IsEnabled { get; set; } = true;

        public FillerWordFilter(IEnumerable<string>? words = null) => SetWords(words ?? Defaults);

        public void SetWords(IEnumerable<string> words)
        {
            _patterns = words
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(w => new Regex(
                    $@"(?<!\w){Regex.Escape(w.Trim())}(?!\w)",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled))
                .ToList();
        }

        public string Apply(string text)
        {
            if (!IsEnabled || string.IsNullOrEmpty(text)) return text;

            foreach (var pattern in _patterns)
                text = pattern.Replace(text, "");

            text = _multiSpace.Replace(text, " ");
            text = _doublePunct.Replace(text, "$1");
            text = _leadingPunct.Replace(text, "");
            return text.Trim();
        }
    }
}
