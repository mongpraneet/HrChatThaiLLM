using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.ObjectPool;
using Microsoft.ML;
using Microsoft.ML.Transforms.Text;

namespace HrChatThaiLLM.Server.Services;

public interface IEmployeeSubIntentPredictionService
{
    string PredictSubIntent(string userMessage);
    void InvalidateCache();
}

public partial class EmployeeSubIntentPredictionService : IEmployeeSubIntentPredictionService
{
    private const float MinimumTopScore = 0.36f;
    private const float MinimumScoreMargin = 0.08f;

    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<EmployeeSubIntentPredictionService> _logger;
    private readonly MLContext _mlContext = new(seed: 1);
    private readonly object _lock = new();

    private ITransformer? _model;
    private ObjectPool<PredictionEngine<IntentData, IntentPrediction>>? _enginePool;
    private bool _isLoaded;

    public EmployeeSubIntentPredictionService(
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<EmployeeSubIntentPredictionService> logger)
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
        }
        PreloadModel();
    }

    public string PredictSubIntent(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return "Unknown";

        if (!_isLoaded || _enginePool == null)
        {
            PreloadModel();
        }

        if (_enginePool == null)
        {
            _logger.LogWarning("Employee sub-intent classifier is not ready. Message={Message}", userMessage);
            return "Unknown";
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
                "Employee sub-intent rejected. PredictedSubIntent={SubIntent}, TopScore={TopScore}, Margin={Margin}, Message={Message}",
                predictedIntent,
                topScore,
                margin,
                userMessage);
            return "Unknown";
        }

        _logger.LogInformation(
            "Employee sub-intent accepted. PredictedSubIntent={SubIntent}, TopScore={TopScore}, Margin={Margin}, Message={Message}",
            predictedIntent,
            topScore,
            margin,
            userMessage);

        return string.IsNullOrWhiteSpace(predictedIntent) ? "Unknown" : predictedIntent;
    }

    private void PreloadModel()
    {
        if (_isLoaded) return;

        lock (_lock)
        {
            if (_isLoaded) return;

            var trainingPath = Path.Combine(_env.WebRootPath ?? "", _config["FilePaths:EmployeeSubIntentTraining"] ?? "file\\Training\\employee_subintent_training_data.csv");
            var modelPath = Path.Combine(_env.WebRootPath ?? "", _config["FilePaths:EmployeeSubIntentModel"] ?? "file\\Modeling\\employee_subintent_model.zip");

            if (!File.Exists(trainingPath))
            {
                _logger.LogWarning("Employee sub-intent training file not found. Path={Path}", trainingPath);
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
                    _logger.LogWarning("Employee sub-intent training file has no usable rows. Path={Path}", trainingPath);
                    return;
                }

                var pipeline = BuildPipeline(_mlContext);
                LogQuickEvaluation(rows, pipeline);

                var finalTrainingData = _mlContext.Data.LoadFromEnumerable(rows);
                _model = pipeline.Fit(finalTrainingData);

                var policy = new PredictionEnginePolicy(_mlContext, _model);
                _enginePool = new DefaultObjectPool<PredictionEngine<IntentData, IntentPrediction>>(policy);

                _mlContext.Model.Save(_model, finalTrainingData.Schema, modelPath);
                _isLoaded = true;

                var labels = rows
                    .Select(row => row.Intent)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(label => label);

                _logger.LogInformation(
                    "Employee sub-intent classifier trained. Rows={RowCount}, Labels={Labels}, Model={ModelPath}",
                    rows.Count,
                    string.Join(", ", labels),
                    modelPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Employee sub-intent classifier failed to load or train.");
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
                MaximumNgramsCount = new[] { 10000 }
            },
            CharFeatureExtractor = null // ปิด char n-gram
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

            _logger.LogInformation("Employee sub-intent classifier loaded from cache. Model={ModelPath}", modelPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Employee sub-intent cache is invalid. Model={ModelPath}", modelPath);
            return false;
        }
    }

    private void LogQuickEvaluation(List<IntentData> rows, IEstimator<ITransformer> pipeline)
    {
        var labelCount = rows.Select(row => row.Intent).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (rows.Count < 30 || labelCount < 2)
        {
            _logger.LogWarning(
                "Employee sub-intent evaluation skipped. Rows={RowCount}, Labels={LabelCount}",
                rows.Count,
                labelCount);
            return;
        }

        var data = _mlContext.Data.LoadFromEnumerable(rows);
        var split = _mlContext.Data.TrainTestSplit(data, testFraction: 0.2, seed: 1);
        var model = pipeline.Fit(split.TrainSet);
        var predictions = model.Transform(split.TestSet);
        var metrics = _mlContext.MulticlassClassification.Evaluate(predictions);

        _logger.LogInformation(
            "Employee sub-intent evaluation: MicroAccuracy={MicroAccuracy:P2}, MacroAccuracy={MacroAccuracy:P2}, LogLoss={LogLoss:F4}",
            metrics.MicroAccuracy,
            metrics.MacroAccuracy,
            metrics.LogLoss);
    }
}