using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.ObjectPool;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.Text;

namespace HrChatThaiLLM.Server.Services;

public class IntentData
{
    [LoadColumn(0)] public string Text { get; set; } = "";
    [LoadColumn(1)] public string Intent { get; set; } = "";
}

public class IntentPrediction
{
    [ColumnName("PredictedLabel")] public string PredictedIntent { get; set; } = "";
    public float[] Score { get; set; } = Array.Empty<float>();
}

public class PredictionEnginePolicy : IPooledObjectPolicy<PredictionEngine<IntentData, IntentPrediction>>
{
    private readonly MLContext _mlContext;
    private readonly ITransformer _model;

    public PredictionEnginePolicy(MLContext mlContext, ITransformer model)
    {
        _mlContext = mlContext;
        _model = model;
    }

    public PredictionEngine<IntentData, IntentPrediction> Create()
    {
        return _mlContext.Model.CreatePredictionEngine<IntentData, IntentPrediction>(_model);
    }

    public bool Return(PredictionEngine<IntentData, IntentPrediction> obj)
    {
        return true;
    }
}

public interface IIntentPredictionService
{
    string PredictIntent(string userMessage);
    void InvalidateCache();
}

public partial class IntentPredictionService : IIntentPredictionService
{
    private const float MinimumTopScore = 0.35f;
    private const float MinimumScoreMargin = 0.08f;

    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<IntentPredictionService> _logger;
    private readonly MLContext _mlContext = new(seed: 0);
    private readonly object _lock = new();

    private ITransformer? _model;
    private ObjectPool<PredictionEngine<IntentData, IntentPrediction>>? _enginePool;
    private bool _isLoaded;


    public IntentPredictionService(IWebHostEnvironment env, IConfiguration config, ILogger<IntentPredictionService> logger)
    {
        _env = env;
        _config = config;
        _logger = logger;
        PreloadModel();
    }

    public void InvalidateCache()
    {
        lock (_lock)
        {
            _isLoaded = false;
            // รีเซตภายใน lock เดียวกัน เพื่อป้องกัน thread อื่นเข้า PreloadModel
            // ก่อนที่การ retrain รอบนี้จะเสร็จ (double-checked locking race condition)
        }
        PreloadModel();
    }

    private void PreloadModel()
    {
        if (_isLoaded) return;

        lock (_lock)
        {
            if (_isLoaded) return;

            var trainingPath = Path.Combine(_env.WebRootPath ?? "", _config["FilePaths:IntentTraining"] ?? "file\\Training\\intent_classifier_training_data.csv");
            var modelPath = Path.Combine(_env.WebRootPath ?? "", _config["FilePaths:IntentModel"] ?? "file\\Modeling\\intent_classifier_model.zip");

            if (!File.Exists(trainingPath))
            {
                _logger.LogWarning("ML.NET intent classifier: training file not found. Path={Path}", trainingPath);
                return;
            }

            try
            {
                if (TryLoadCachedModel(trainingPath, modelPath))
                {
                    _isLoaded = true;
                    return;
                }

                var trainingData = _mlContext.Data.LoadFromTextFile<IntentData>(
                    path: trainingPath,
                    hasHeader: true,
                    separatorChar: ',',
                    allowQuoting: true);

                var rows = _mlContext.Data.CreateEnumerable<IntentData>(trainingData, reuseRowObject: false)
                    .Where(row => !string.IsNullOrWhiteSpace(row.Text) && !string.IsNullOrWhiteSpace(row.Intent))
                    .Select(row => new IntentData
                    {
                        Text = TextNormalizationHelper.NormalizeText(row.Text),
                        Intent = row.Intent.Trim()
                    })
                    .Where(row => !string.IsNullOrWhiteSpace(row.Text) && !string.IsNullOrWhiteSpace(row.Intent))
                    .GroupBy(row => $"{row.Text}\u001f{row.Intent}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                if (rows.Count == 0)
                {
                    _logger.LogWarning("ML.NET intent classifier: training file has no usable rows. Path={Path}", trainingPath);
                    return;
                }

                var labels = rows
                    .Select(row => row.Intent)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(label => label)
                    .ToArray();

                var pipeline = BuildPipeline(_mlContext);
                LogQuickEvaluation(rows, pipeline);

                var finalTrainingData = _mlContext.Data.LoadFromEnumerable(rows);
                _model = pipeline.Fit(finalTrainingData);

                var policy = new PredictionEnginePolicy(_mlContext, _model);
                _enginePool = new DefaultObjectPool<PredictionEngine<IntentData, IntentPrediction>>(policy);

                _mlContext.Model.Save(_model, finalTrainingData.Schema, modelPath);
                _isLoaded = true;

                _logger.LogInformation(
                    "ML.NET intent classifier trained. Path={Path}, Rows={RowCount}, Labels={Labels}, Model={ModelPath}",
                    trainingPath,
                    rows.Count,
                    string.Join(", ", labels),
                    modelPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ML.NET intent classifier failed to load or train.");
            }
        }
    }

    private static IEstimator<ITransformer> BuildPipeline(MLContext mlContext) // (หรือแบบไม่มีพารามิเตอร์ ตามแต่ละไฟล์)
    {
        var options = new TextFeaturizingEstimator.Options
        {
            WordFeatureExtractor = new WordBagEstimator.Options
            {
                NgramLength = 2,
                // แก้ไขบรรทัดนี้: ใส่ new[] { ... } ครอบตัวเลขไว้
                MaximumNgramsCount = new[] { 2000, 2000 }
            },
            CharFeatureExtractor = new WordBagEstimator.Options
            {
                NgramLength = 3,
                UseAllLengths = true,           // รวม char 1-gram, 2-gram, 3-gram
                MaximumNgramsCount = new[] { 2000, 2000, 2000 }  // [1-char, 2-char, 3-char]
            }
        };

        return mlContext.Transforms.Conversion.MapValueToKey("Label", nameof(IntentData.Intent))
            .Append(mlContext.Transforms.Text.FeaturizeText("Features", options, nameof(IntentData.Text)))
            .Append(mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy("Label", "Features"))
            .Append(mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
    }

    private bool TryLoadCachedModel(string trainingPath, string modelPath)
    {
        if (!File.Exists(modelPath)) return false;
        if (File.GetLastWriteTimeUtc(modelPath) < File.GetLastWriteTimeUtc(trainingPath)) return false;

        try
        {
            _model = _mlContext.Model.Load(modelPath, out _);

            var policy = new PredictionEnginePolicy(_mlContext, _model);
            _enginePool = new DefaultObjectPool<PredictionEngine<IntentData, IntentPrediction>>(policy);

            _logger.LogInformation("ML.NET intent classifier loaded from cache. Model={ModelPath}", modelPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ML.NET intent classifier cache is invalid. Model={ModelPath}", modelPath);
            return false;
        }
    }

    private void LogQuickEvaluation(List<IntentData> rows, IEstimator<ITransformer> pipeline)
    {
        var labelCount = rows.Select(row => row.Intent).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (rows.Count < 30 || labelCount < 2)
        {
            _logger.LogWarning(
                "ML.NET intent classifier: skipped evaluation because data is too small. Rows={RowCount}, Labels={LabelCount}",
                rows.Count,
                labelCount);
            return;
        }

        var data = _mlContext.Data.LoadFromEnumerable(rows);
        var split = _mlContext.Data.TrainTestSplit(data, testFraction: 0.2, seed: 0);
        var model = pipeline.Fit(split.TrainSet);
        var predictions = model.Transform(split.TestSet);
        var metrics = _mlContext.MulticlassClassification.Evaluate(predictions);

        _logger.LogInformation(
            "ML.NET intent classifier evaluation: MicroAccuracy={MicroAccuracy:P2}, MacroAccuracy={MacroAccuracy:P2}, LogLoss={LogLoss:F4}",
            metrics.MicroAccuracy,
            metrics.MacroAccuracy,
            metrics.LogLoss);
    }

    public string PredictIntent(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return "OutOfScope";

        if (!_isLoaded || _enginePool == null)
            PreloadModel();

        if (_enginePool == null)
        {
            _logger.LogWarning("ML.NET intent classifier is not ready. Message={Message}", userMessage);
            return "OutOfScope";
        }

        IntentPrediction prediction;
        var engine = _enginePool.Get();
        try
        {
            prediction = engine.Predict(new IntentData { Text = TextNormalizationHelper.NormalizeText(userMessage) });
        }
        finally
        {
            _enginePool.Return(engine);
        }

        var predictedIntent = prediction.PredictedIntent?.Trim();
        var orderedScores = prediction.Score.OrderByDescending(score => score).ToArray();
        var topScore = orderedScores.Length > 0 ? orderedScores[0] : 0f;
        var secondScore = orderedScores.Length > 1 ? orderedScores[1] : 0f;
        var margin = topScore - secondScore;

        if (topScore < MinimumTopScore || margin < MinimumScoreMargin)
        {
            _logger.LogInformation(
                "ML.NET intent classifier rejected low-confidence prediction. PredictedIntent={PredictedIntent}, TopScore={TopScore}, Margin={Margin}, Message={Message}",
                predictedIntent, topScore, margin, userMessage);
            return "OutOfScope";
        }

        _logger.LogInformation(
            "ML.NET intent classifier accepted prediction. PredictedIntent={PredictedIntent}, TopScore={TopScore}, Margin={Margin}, Message={Message}",
            predictedIntent, topScore, margin, userMessage);

        return string.IsNullOrWhiteSpace(predictedIntent) ? "OutOfScope" : predictedIntent;
    }
}