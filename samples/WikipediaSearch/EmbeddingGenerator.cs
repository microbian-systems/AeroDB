using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Runtime.InteropServices;

/// <summary>
/// Generates 384-dimensional text embeddings using the all-MiniLM-L6-v2 ONNX model.
/// Loads the model and vocabulary from disk on construction, then reuses across all
/// inference calls. Tokenization implements the BERT WordPiece algorithm (basic subset
/// sufficient for English prose).
///
/// Input discovery is fully dynamic: input/output names are matched by convention so the
/// same code works across different BERT-family ONNX exports.
///
/// Resilience: GenerateBatch tries batched inference first, falls back to one-at-a-time,
/// and if even that fails, produces a deterministic hash-based embedding so the demo
/// never crashes due to embedding issues.
/// </summary>
public sealed class EmbeddingGenerator : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputIdsName;
    private readonly string _attnMaskName;
    private readonly string? _typeIdsName;
    private readonly string _outputName;
    private readonly int _hiddenDim;
    private readonly Dictionary<string, int> _vocab;
    private readonly int _maxLength;

    // BERT special token IDs
    private const int CLS_ID = 101;
    private const int SEP_ID = 102;
    private const int UNK_ID = 100;
    private const int VOCAB_SIZE = 30522;

    public int Dimension => _hiddenDim;

    /// <param name="modelDir">Directory containing model.onnx, vocab.txt, and config.json.</param>
    public EmbeddingGenerator(string modelDir)
    {
        var modelPath = Path.Combine(modelDir, "model.onnx");
        var vocabPath = Path.Combine(modelDir, "vocab.txt");
        var configPath = Path.Combine(modelDir, "config.json");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"ONNX model not found at {modelPath}. " +
                "Download from https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx/resolve/main/model.onnx");
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException(
                $"vocab.txt not found at {vocabPath}. " +
                "Download from https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx");

        _vocab = LoadVocab(vocabPath);
        _maxLength = File.Exists(configPath) ? LoadMaxLength(configPath) : 256;

        _session = new InferenceSession(modelPath, MakeSessionOptions());

        // ── Log model metadata for diagnostics ──
        Console.WriteLine($"  [Embedding] Model inputs ({_session.InputNames.Count}):");
        foreach (var kvp in _session.InputMetadata)
        {
            var dims = string.Join(", ", kvp.Value.Dimensions.Select(d => d == 0 ? "?" : d.ToString()));
            Console.WriteLine($"    {kvp.Key}: [{dims}], type={kvp.Value.ElementType}");
        }
        Console.WriteLine($"  [Embedding] Model outputs ({_session.OutputNames.Count}):");
        foreach (var kvp in _session.OutputMetadata)
        {
            var dims = string.Join(", ", kvp.Value.Dimensions.Select(d => d == 0 ? "?" : d.ToString()));
            Console.WriteLine($"    {kvp.Key}: [{dims}], type={kvp.Value.ElementType}");
        }

        // ── Discover input names by convention (dynamic, model-agnostic) ──
        var inputNames = _session.InputNames.ToArray();
        _inputIdsName = GetRequiredInput(inputNames, "input_ids", "input");
        _attnMaskName = GetRequiredInput(inputNames, "attention_mask", "mask", "attn_mask");
        _typeIdsName = inputNames.FirstOrDefault(n =>
            n.Contains("token_type_ids", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("type_ids", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("segment", StringComparison.OrdinalIgnoreCase));

        if (_typeIdsName != null)
            Console.WriteLine($"  [Embedding] Model has token_type_ids input — will pass all 3 inputs");

        // ── Discover output — prefer sentence_embedding (already pooled) ──
        var outputNames = _session.OutputNames.ToArray();
        _outputName = outputNames.FirstOrDefault(n =>
            n.Contains("sentence_embedding", StringComparison.OrdinalIgnoreCase))
            ?? outputNames[0];

        // ── Read hidden dimension from output shape (not hardcoded) ──
        var outputMeta = _session.OutputMetadata[_outputName];
        _hiddenDim = outputMeta.Dimensions[^1];

        Console.WriteLine($"  [Embedding] Model loaded — {_hiddenDim}-dim, max_length={_maxLength}, output=\"{_outputName}\"");
    }

    private static string GetRequiredInput(string[] names, params string[] patterns)
    {
        foreach (var p in patterns)
        {
            var match = names.FirstOrDefault(n =>
                n.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        throw new InvalidOperationException(
            $"Model missing required input matching [{string.Join(", ", patterns)}]. " +
            $"Available: [{string.Join(", ", names)}]");
    }

    // ────────────────────────────────────────────────────
    //  Public API
    // ────────────────────────────────────────────────────

    /// <summary>Generate a single embedding for a text string.</summary>
    public float[] Generate(string text)
    {
        var (tokenIds, attentionMask, tokenTypeIds) = Tokenize(text);
        return RunInferenceSingle(tokenIds, attentionMask,
            _typeIdsName != null ? tokenTypeIds : null);
    }

    /// <summary>Generate embeddings for multiple texts. Tries batched inference first,
    /// falls back to one-at-a-time, and if even that fails, generates a deterministic
    /// hash-based embedding so the demo never crashes from inference issues.</summary>
    public float[][] GenerateBatch(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return [];
        if (texts.Count == 1) return [Generate(texts[0])];

        try
        {
            return TryBatchedInference(texts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Embedding] Batch inference failed ({ex.GetType().Name}), falling back to single...");
        }

        // Single-item fallback with per-item hash guard
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
        {
            try
            {
                results[i] = Generate(texts[i]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [Embedding] Single inference failed for item {i} ({ex.GetType().Name}), using hash fallback...");
                results[i] = GenerateHashEmbedding(texts[i]);
            }
        }
        return results;
    }

    // ────────────────────────────────────────────────────
    //  Batched ONNX Inference
    // ────────────────────────────────────────────────────

    private float[][] TryBatchedInference(IReadOnlyList<string> texts)
    {
        var batchSize = texts.Count;
        var seqLen = _maxLength;
        var flatTokens = new long[batchSize * seqLen];
        var flatMask = new long[batchSize * seqLen];
        long[]? flatTypeIds = _typeIdsName != null ? new long[batchSize * seqLen] : null;

        for (int i = 0; i < texts.Count; i++)
        {
            var (tok, mask, type) = Tokenize(texts[i]);
            Array.Copy(tok, 0, flatTokens, i * seqLen, seqLen);
            Array.Copy(mask, 0, flatMask, i * seqLen, seqLen);
            if (flatTypeIds != null)
                Array.Copy(type, 0, flatTypeIds, i * seqLen, seqLen);
        }

        var shape = new[] { batchSize, seqLen };
        var inputs = new List<NamedOnnxValue>(_typeIdsName != null ? 3 : 2)
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, new DenseTensor<long>(flatTokens, shape)),
            NamedOnnxValue.CreateFromTensor(_attnMaskName, new DenseTensor<long>(flatMask, shape)),
        };
        if (_typeIdsName != null && flatTypeIds != null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_typeIdsName, new DenseTensor<long>(flatTypeIds, shape)));
        }

        using var results = _session.Run(inputs);
        return ExtractAllEmbeddings(results, batchSize);
    }

    public void Dispose() => _session.Dispose();

    // ────────────────────────────────────────────────────
    //  Single ONNX Inference
    // ────────────────────────────────────────────────────

    private float[] RunInferenceSingle(long[] tokenIds, long[] attnMask, long[]? typeIds)
    {
        var shape = new[] { 1, _maxLength };

        var inputs = new List<NamedOnnxValue>(_typeIdsName != null ? 3 : 2)
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName,
                new DenseTensor<long>(tokenIds, shape)),
            NamedOnnxValue.CreateFromTensor(_attnMaskName,
                new DenseTensor<long>(attnMask, shape)),
        };
        if (_typeIdsName != null && typeIds != null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_typeIdsName,
                new DenseTensor<long>(typeIds, shape)));
        }

        using var results = _session.Run(inputs);
        return ExtractEmbedding(results);
    }

    // ────────────────────────────────────────────────────
    //  Result Extraction
    // ────────────────────────────────────────────────────

    private float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        // Look up output by name instead of assuming positional order
        var output = results.Single(r => r.Name == _outputName);
        var raw = output.AsEnumerable<float>().ToArray();

        // If the output is already the embedding vector (pooled), return normalized
        if (raw.Length == _hiddenDim)
            return Normalize(raw);

        // Raw hidden states: [1, seq_len, hidden_dim] — mean-pool over sequence
        var seqLen = raw.Length / _hiddenDim;
        var pooled = new float[_hiddenDim];
        for (int s = 0; s < seqLen; s++)
        {
            var offset = s * _hiddenDim;
            for (int d = 0; d < _hiddenDim; d++)
                pooled[d] += raw[offset + d] / seqLen;
        }
        return Normalize(pooled);
    }

    private float[][] ExtractAllEmbeddings(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, int batchSize)
    {
        // Look up output by name instead of assuming positional order
        var output = results.Single(r => r.Name == _outputName);
        var raw = output.AsEnumerable<float>().ToArray();

        var perSample = raw.Length / batchSize;
        var embeddings = new float[batchSize][];

        for (int i = 0; i < batchSize; i++)
        {
            if (perSample == _hiddenDim)
            {
                // Pooled output: direct copy
                embeddings[i] = new float[_hiddenDim];
                Array.Copy(raw, i * _hiddenDim, embeddings[i], 0, _hiddenDim);
            }
            else
            {
                // Hidden states: mean-pool over sequence
                var seqLen = perSample / _hiddenDim;
                var pooled = new float[_hiddenDim];
                var batchOffset = i * perSample;
                for (int s = 0; s < seqLen; s++)
                {
                    var tokOffset = batchOffset + s * _hiddenDim;
                    for (int d = 0; d < _hiddenDim; d++)
                        pooled[d] += raw[tokOffset + d] / seqLen;
                }
                embeddings[i] = pooled;
            }
            embeddings[i] = Normalize(embeddings[i]);
        }

        return embeddings;
    }

    // ────────────────────────────────────────────────────
    //  Hash-based Fallback (last resort, never crashes)
    // ────────────────────────────────────────────────────

    private float[] GenerateHashEmbedding(string text)
    {
        // FNV-1a hash — deterministic, no dependencies, seeded from text
        const ulong fnvOffsetBasis = 14695981039346656037;
        const ulong fnvPrime = 1099511628211;

        ulong hash = fnvOffsetBasis;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= fnvPrime;
        }

        var seed = (int)(hash ^ (hash >> 32));
        var rng = new Random(seed);
        var emb = new float[_hiddenDim];
        for (int i = 0; i < _hiddenDim; i++)
            emb[i] = (float)(rng.NextDouble() * 2.0 - 1.0); // uniform [-1, 1]

        return Normalize(emb);
    }

    // ────────────────────────────────────────────────────
    //  BERT WordPiece Tokenization
    // ────────────────────────────────────────────────────

    private (long[] tokenIds, long[] attentionMask, long[] typeIds) Tokenize(string text)
    {
        text = text.ToLowerInvariant().Trim();
        if (text.Length == 0) text = " ";
        if (text.Length > 2048) text = text[..2048];

        var words = SplitWords(text);

        var tokens = new List<long> { CLS_ID };
        foreach (var word in words)
            TokenizeWord(word, tokens);

        // Truncate: leave room for [SEP]
        var maxTokens = _maxLength - 1;
        if (tokens.Count > maxTokens)
            tokens = tokens.Take(maxTokens).ToList();

        tokens.Add(SEP_ID);

        // Create padded tensors
        var seqLen = tokens.Count;
        var padded = new long[_maxLength];
        var mask = new long[_maxLength];
        var typeIdsArr = new long[_maxLength];

        for (int i = 0; i < seqLen; i++)
        {
            padded[i] = tokens[i];
            mask[i] = 1;
            // typeIds remain 0 (single-segment)
        }

        return (padded, mask, typeIdsArr);
    }

    private void TokenizeWord(string word, List<long> tokens)
    {
        if (string.IsNullOrEmpty(word)) return;

        if (_vocab.TryGetValue(word, out var id))
        {
            tokens.Add(id);
            return;
        }

        var chars = word.AsSpan();
        var start = 0;
        while (start < chars.Length)
        {
            var found = false;
            for (int end = chars.Length; end > start; end--)
            {
                var sub = start == 0
                    ? chars[start..end].ToString()
                    : "##" + chars[start..end].ToString();

                if (_vocab.TryGetValue(sub, out var subId))
                {
                    tokens.Add(subId);
                    start = end;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                tokens.Add(UNK_ID);
                start++;
            }
        }
    }

    private static string[] SplitWords(string text)
    {
        return text.Split([' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?',
            '"', '\'', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_', '+',
            '=', '*', '&', '^', '%', '$', '#', '@', '~', '`', '<', '>', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // ────────────────────────────────────────────────────
    //  Vocabulary & Config
    // ────────────────────────────────────────────────────

    private static Dictionary<string, int> LoadVocab(string path)
    {
        var vocab = new Dictionary<string, int>(VOCAB_SIZE);
        var lines = File.ReadLines(path);
        int idx = 0;
        foreach (var line in lines)
        {
            var token = line.Trim();
            if (token.Length > 0)
                vocab[token] = idx++;
        }
        return vocab;
    }

    private static int LoadMaxLength(string configPath)
    {
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(configPath));
            if (json.RootElement.TryGetProperty("max_position_embeddings", out var max))
                return max.GetInt32();
        }
        catch { }
        return 256;
    }

    private static SessionOptions MakeSessionOptions()
    {
        var opts = new SessionOptions();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { opts.AppendExecutionProvider_DML(); }
            catch { /* DML not available, CPU only */ }
        }
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        return opts;
    }

    private static float[] Normalize(float[] v)
    {
        var sumSq = 0.0;
        for (int i = 0; i < v.Length; i++)
            sumSq += (double)v[i] * v[i];
        var norm = Math.Sqrt(sumSq);
        if (norm > 0)
        {
            for (int i = 0; i < v.Length; i++)
                v[i] = (float)(v[i] / norm);
        }
        return v;
    }
}
