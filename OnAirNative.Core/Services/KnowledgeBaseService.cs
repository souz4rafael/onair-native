using System.Text;
using System.Text.RegularExpressions;
using OnAirNative.Models;

namespace OnAirNative.Services;

/// <summary>
/// Lightweight keyword/TF-IDF-style relevance search over small user-attached reference
/// documents (<see cref="AppConfig.KnowledgeBaseFiles"/>) — deliberately NOT an embeddings/
/// vector-DB pipeline. For the realistic use case here (a handful of small personal reference
/// docs — product spec sheets, FAQs, pricing sheets — searched live during a real-time Q&amp;A
/// exchange), a heavyweight embeddings pipeline would add real API cost, network latency, and an
/// external dependency for no meaningful accuracy gain over simple term-overlap scoring on a
/// small corpus. This keeps search fully local, deterministic, and instant — no extra AI call,
/// no extra network round-trip, nothing to configure beyond picking files.
///
/// Scope: plain text and Markdown files only (.txt/.md) — no .docx/.pdf/.xlsx parsing, which
/// would need a heavy dependency for a corpus this small; convert those to plain text first.
///
/// A returned excerpt is injected into the chat system prompt as-is (see AiChatService's
/// knowledgeBaseContext parameter) — this class only decides WHICH excerpts are relevant enough
/// to include, never rewrites/summarizes them.
/// </summary>
public sealed class KnowledgeBaseService
{
    private sealed record CachedDoc(DateTime LastWriteUtc, IReadOnlyList<string> Chunks);

    // Keyed by file path — re-read from disk only when the file's LastWriteTimeUtc changes, so
    // repeated questions against the same unmodified files don't re-read/re-chunk every time
    // (documents may live on a network share), while external edits are still picked up on the
    // very next question with no explicit "reload" step needed.
    private readonly Dictionary<string, CachedDoc> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Minimal built-in stopword list (English + Portuguese, since this app's Q&A already
    // supports "respond in the same language as the question" for either) — used ONLY to
    // improve scoring quality on small corpora; not exhaustive, not configurable, deliberately
    // low-maintenance. IDF already naturally down-weights ubiquitous terms, so this is a modest
    // quality boost, not a hard requirement.
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","is","are","was","were","be","been","being","of","and","or","to","in","on",
        "at","for","with","as","by","from","that","this","these","those","it","its","if","not",
        "no","do","does","did","can","could","will","would","should","what","which","who","how",
        "we","you","i","he","she","they","them","his","her","their","our","your","my",
        "o","os","as","de","do","da","dos","das","em","um","uma","uns","umas","para","com",
        "não","é","são","foi","foram","ser","estar","que","se","na","no","nas","nos","por","mais",
        "mas","ou","e","ao","à","aos","às","como","quando","onde","qual","quais","quem","seu","sua",
        "seus","suas","eu","tu","ele","ela","nós","eles","elas","meu","minha","teu","tua",
    };

    /// <summary>Returns up to <paramref name="maxChunks"/> of the most relevant excerpts from
    /// <see cref="AppConfig.KnowledgeBaseFiles"/> for the given question, joined into one string
    /// ready to inject into the chat context — or "" when no files are configured, no file is
    /// readable, or nothing scores above zero relevance (a question with no real overlap gets NO
    /// reference material injected, rather than a random/irrelevant excerpt padding the prompt).
    /// Combined length is capped at <paramref name="maxTotalChars"/> so one large match can't
    /// blow up the request's context/cost budget.</summary>
    public string BuildContextForQuestion(string question, AppConfig cfg, int maxChunks = 3, int maxTotalChars = 1500)
    {
        if (cfg.KnowledgeBaseFiles is not { Count: > 0 } || string.IsNullOrWhiteSpace(question))
            return "";

        var allChunks = new List<string>();
        foreach (var path in cfg.KnowledgeBaseFiles)
            allChunks.AddRange(GetChunks(path));

        if (allChunks.Count == 0) return "";

        var questionTerms = Tokenize(question).Distinct().ToList();
        if (questionTerms.Count == 0) return "";

        // Document frequency across all chunks (for IDF) — recomputed fresh per call. Cheap for
        // a realistically small personal corpus (dozens of chunks, not thousands); no need for
        // a persistent index given how infrequently a live Q&A asks questions (at most every
        // few seconds, never in a tight loop).
        var chunkTermFreqs = new List<Dictionary<string, int>>(allChunks.Count);
        var df = new Dictionary<string, int>();
        foreach (var chunk in allChunks)
        {
            var tf = new Dictionary<string, int>();
            foreach (var term in Tokenize(chunk))
                tf[term] = tf.GetValueOrDefault(term) + 1;
            chunkTermFreqs.Add(tf);

            foreach (var term in tf.Keys)
                df[term] = df.GetValueOrDefault(term) + 1;
        }

        var n = allChunks.Count;
        var scored = new List<(double Score, string Text)>();
        for (int i = 0; i < allChunks.Count; i++)
        {
            double score = 0;
            foreach (var qTerm in questionTerms)
            {
                if (!chunkTermFreqs[i].TryGetValue(qTerm, out var termFreq)) continue;
                // Smoothed IDF: log(1 + N/df) instead of the classic log(N/df). The classic
                // formula is designed for large corpora — for the realistic use case here (a
                // personal knowledge base of maybe 1-10 small chunks), N/df is often <= 1,
                // making classic IDF zero or NEGATIVE for every term and silently discarding all
                // matches (caught by a real failing test against a single-chunk document). The
                // "+1" guarantees a strictly positive score for every shared term regardless of
                // corpus size, while still weighting rarer terms (small df) higher than
                // ubiquitous ones (large df) — the property TF-IDF is actually meant to capture.
                var idf = Math.Log(1.0 + (double)n / df.GetValueOrDefault(qTerm, 1));
                score += termFreq * idf;
            }
            if (score > 0)
                scored.Add((score, allChunks[i]));
        }

        if (scored.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var (_, text) in scored.OrderByDescending(s => s.Score).Take(maxChunks))
        {
            var toAdd = text;
            var remaining = maxTotalChars - sb.Length - (sb.Length > 0 ? 5 : 0); // 5 ≈ separator length
            if (remaining <= 50) break; // not enough budget left for a meaningful excerpt
            if (toAdd.Length > remaining)
                toAdd = toAdd[..remaining] + "…";

            if (sb.Length > 0) sb.Append("\n\n---\n\n");
            sb.Append(toAdd);
            if (sb.Length >= maxTotalChars) break;
        }
        return sb.ToString();
    }

    private IReadOnlyList<string> GetChunks(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return [];
            var lastWrite = info.LastWriteTimeUtc;

            if (_cache.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWrite)
                return cached.Chunks;

            var text = File.ReadAllText(path);
            var chunks = ChunkText(text);
            _cache[path] = new CachedDoc(lastWrite, chunks);
            return chunks;
        }
        catch
        {
            // Unreadable file (locked, permission denied, deleted mid-read, etc.) — skip it
            // silently. A knowledge-base lookup must never fail the whole Q&A call over one bad
            // file; the other configured files (if any) still get searched normally.
            return [];
        }
    }

    /// <summary>Splits raw document text into paragraph-sized chunks (blank-line-separated),
    /// further splitting any paragraph longer than <paramref name="maxChunkChars"/> on sentence
    /// boundaries so no single chunk is too large to usefully inject or to dominate scoring by
    /// sheer size alone. Internal (not private) so unit tests can exercise chunking directly
    /// without needing a real file on disk.</summary>
    internal static IReadOnlyList<string> ChunkText(string text, int maxChunkChars = 900)
    {
        var paragraphs = Regex.Split(text, @"\r?\n\s*\r?\n")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0);

        var chunks = new List<string>();
        foreach (var para in paragraphs)
        {
            if (para.Length <= maxChunkChars)
            {
                chunks.Add(para);
                continue;
            }

            // Greedily accumulate sentences up to maxChunkChars per sub-chunk, so a long
            // paragraph splits at sentence boundaries rather than mid-sentence.
            var sentences = Regex.Split(para, @"(?<=[.!?])\s+");
            var current = new StringBuilder();
            foreach (var sentence in sentences)
            {
                if (sentence.Length > maxChunkChars)
                {
                    // A single "sentence" itself exceeds the cap (e.g. no punctuation at all) —
                    // flush whatever's accumulated, then hard-cut this one as a last resort.
                    if (current.Length > 0) { chunks.Add(current.ToString().Trim()); current.Clear(); }
                    for (var i = 0; i < sentence.Length; i += maxChunkChars)
                        chunks.Add(sentence.Substring(i, Math.Min(maxChunkChars, sentence.Length - i)));
                    continue;
                }
                if (current.Length > 0 && current.Length + sentence.Length + 1 > maxChunkChars)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }
                current.Append(sentence).Append(' ');
            }
            if (current.Length > 0) chunks.Add(current.ToString().Trim());
        }
        return chunks;
    }

    /// <summary>Lowercases, strips light Markdown syntax and punctuation, splits on whitespace,
    /// and removes common English/Portuguese stopwords. Used ONLY for scoring — the original raw
    /// text (markup included) is what actually gets injected into the AI's context, never this
    /// tokenized form. Internal so unit tests can exercise it directly.</summary>
    internal static List<string> Tokenize(string text)
    {
        var stripped = Regex.Replace(text, @"[#*_`>\-]", " ");
        return Regex.Matches(stripped.ToLowerInvariant(), @"[\p{L}\p{Nd}]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !Stopwords.Contains(t))
            .ToList();
    }
}
