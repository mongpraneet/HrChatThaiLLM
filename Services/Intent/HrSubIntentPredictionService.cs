using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.ObjectPool;
using Microsoft.ML;
using Microsoft.ML.Transforms.Text;
using System.Collections.Concurrent;

namespace HrChatThaiLLM.Server.Services;

public interface IHrSubIntentPredictionService
{
    string PredictSubIntent(string domain, string userMessage);
    void InvalidateCache(string domain);
}

public partial class HrSubIntentPredictionService : IHrSubIntentPredictionService
{
    private const float MinimumTopScore = 0.36f;
    private const float MinimumScoreMargin = 0.08f;

    private static readonly IReadOnlyDictionary<string, string> DomainFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["attendance"] = "attendance_subintent_training_data.csv",
            ["csvintent"] = "csvintent_subintent_training_data.csv",
            ["leave"] = "leave_subintent_training_data.csv",
            ["medical"] = "medical_subintent_training_data.csv",
            ["medical_regulation"] = "medical_regulation_subintent_training_data.csv",
            ["training"] = "training_subintent_training_data.csv"
        };

    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<HrSubIntentPredictionService> _logger;
    private readonly ConcurrentDictionary<string, ModelState> _states = new(StringComparer.OrdinalIgnoreCase);

    public HrSubIntentPredictionService(
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<HrSubIntentPredictionService> logger)
    {
        _env = env;
        _config = config;
        _logger = logger;
    }

    public void InvalidateCache(string domain)
    {
        if (_states.TryGetValue(domain.Trim(), out var state))
        {
            lock (state.LoadLock)
            {
                state.IsLoaded = false;
            }
            EnsureLoaded(domain.Trim(), state);
        }
    }

    public string PredictSubIntent(string domain, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(userMessage))
        {
            return "Unknown";
        }

        var state = _states.GetOrAdd(domain.Trim(), key => new ModelState(new MLContext(seed: StableSeed(key))));
        EnsureLoaded(domain.Trim(), state);

        if (state.EnginePool == null)
        {
            return "Unknown";
        }

        IntentPrediction prediction;
        var engine = state.EnginePool.Get();
        try
        {
            prediction = engine.Predict(new IntentData { Text = TextNormalizationHelper.NormalizeText(userMessage) });
        }
        finally
        {
            state.EnginePool.Return(engine);
        }

        var predictedIntent = prediction.PredictedIntent?.Trim();
        var orderedScores = prediction.Score.OrderByDescending(score => score).ToArray();
        var topScore = orderedScores.Length > 0 ? orderedScores[0] : 0f;
        var secondScore = orderedScores.Length > 1 ? orderedScores[1] : 0f;
        var margin = topScore - secondScore;

        if (topScore < MinimumTopScore || margin < MinimumScoreMargin)
        {
            _logger.LogInformation(
                "HR sub-intent rejected. Domain={Domain}, SubIntent={SubIntent}, TopScore={TopScore}, Margin={Margin}, Message={Message}",
                domain,
                predictedIntent,
                topScore,
                margin,
                userMessage);
            return "Unknown";
        }

        _logger.LogInformation(
            "HR sub-intent accepted. Domain={Domain}, SubIntent={SubIntent}, TopScore={TopScore}, Margin={Margin}, Message={Message}",
            domain,
            predictedIntent,
            topScore,
            margin,
            userMessage);

        return string.IsNullOrWhiteSpace(predictedIntent) ? "Unknown" : predictedIntent;
    }

    private void EnsureLoaded(string domain, ModelState state)
    {
        if (state.IsLoaded) return;

        lock (state.LoadLock)
        {
            if (state.IsLoaded) return;

            if (!DomainFiles.TryGetValue(domain, out var csvFile))
            {
                _logger.LogWarning("HR sub-intent domain is not registered. Domain={Domain}", domain);
                state.IsLoaded = true;
                return;
            }

            var trainingDir = _config["FilePaths:HrSubIntentTrainingDir"] ?? "file\\Training";
            var modelDir = _config["FilePaths:HrSubIntentModelDir"] ?? "file\\Modeling";

            var trainingPath = Path.Combine(_env.WebRootPath ?? "", trainingDir, csvFile);
            var modelPath = Path.Combine(_env.WebRootPath ?? "", modelDir, Path.ChangeExtension(csvFile, ".model.zip"));

            if (!File.Exists(trainingPath))
            {
                _logger.LogWarning("HR sub-intent training file not found. Domain={Domain}, Path={Path}", domain, trainingPath);
                state.IsLoaded = true;
                return;
            }

            try
            {
                if (TryLoadCachedModel(state, trainingPath, modelPath))
                {
                    state.IsLoaded = true;
                    return;
                }

                var trainingData = state.MlContext.Data.LoadFromTextFile<IntentData>(
                    path: trainingPath,
                    hasHeader: true,
                    separatorChar: ',',
                    allowQuoting: true);

                var rows = state.MlContext.Data.CreateEnumerable<IntentData>(trainingData, reuseRowObject: false)
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
                    _logger.LogWarning("HR sub-intent training file has no usable rows. Domain={Domain}, Path={Path}", domain, trainingPath);
                    state.IsLoaded = true;
                    return;
                }

                var pipeline = BuildPipeline(state.MlContext);
                LogQuickEvaluation(domain, state.MlContext, rows, pipeline);

                var finalTrainingData = state.MlContext.Data.LoadFromEnumerable(rows);
                state.Model = pipeline.Fit(finalTrainingData);

                var policy = new PredictionEnginePolicy(state.MlContext, state.Model);
                state.EnginePool = new DefaultObjectPool<PredictionEngine<IntentData, IntentPrediction>>(policy);

                state.MlContext.Model.Save(state.Model, finalTrainingData.Schema, modelPath);
                state.IsLoaded = true;

                var labels = rows
                    .Select(row => row.Intent)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(label => label);

                _logger.LogInformation(
                    "HR sub-intent classifier trained. Domain={Domain}, Rows={RowCount}, Labels={Labels}, Model={ModelPath}",
                    domain,
                    rows.Count,
                    string.Join(", ", labels),
                    modelPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HR sub-intent classifier failed. Domain={Domain}", domain);
                state.IsLoaded = true;
            }
        }
    }

    private static IEstimator<ITransformer> BuildPipeline(MLContext mlContext)
    {
        var options = new TextFeaturizingEstimator.Options
        {
            WordFeatureExtractor = new WordBagEstimator.Options
            {
                NgramLength = 2,
                MaximumNgramsCount = new[] { 10000 }
            },
            CharFeatureExtractor = null // ปิด char n-gram
        };

        return mlContext.Transforms.Conversion.MapValueToKey("Label", nameof(IntentData.Intent))
            .Append(mlContext.Transforms.Text.FeaturizeText("Features", options, nameof(IntentData.Text)))
            .Append(mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy("Label", "Features"))
            .Append(mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
    }

    private bool TryLoadCachedModel(ModelState state, string trainingPath, string modelPath)
    {
        if (!File.Exists(modelPath)) return false;
        if (File.GetLastWriteTimeUtc(modelPath) < File.GetLastWriteTimeUtc(trainingPath)) return false;

        try
        {
            state.Model = state.MlContext.Model.Load(modelPath, out _);

            var policy = new PredictionEnginePolicy(state.MlContext, state.Model);
            state.EnginePool = new DefaultObjectPool<PredictionEngine<IntentData, IntentPrediction>>(policy);

            _logger.LogInformation("HR sub-intent classifier loaded from cache. Model={ModelPath}", modelPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HR sub-intent cache is invalid. Model={ModelPath}", modelPath);
            return false;
        }
    }

    private void LogQuickEvaluation(
        string domain,
        MLContext mlContext,
        List<IntentData> rows,
        IEstimator<ITransformer> pipeline)
    {
        var labelCount = rows.Select(row => row.Intent).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (rows.Count < 30 || labelCount < 2)
        {
            _logger.LogWarning(
                "HR sub-intent evaluation skipped. Domain={Domain}, Rows={RowCount}, Labels={LabelCount}",
                domain,
                rows.Count,
                labelCount);
            return;
        }

        var data = mlContext.Data.LoadFromEnumerable(rows);
        var split = mlContext.Data.TrainTestSplit(data, testFraction: 0.2, seed: StableSeed(domain));
        var model = pipeline.Fit(split.TrainSet);
        var predictions = model.Transform(split.TestSet);
        var metrics = mlContext.MulticlassClassification.Evaluate(predictions);

        _logger.LogInformation(
            "HR sub-intent evaluation. Domain={Domain}, MicroAccuracy={MicroAccuracy:P2}, MacroAccuracy={MacroAccuracy:P2}, LogLoss={LogLoss:F4}",
            domain,
            metrics.MicroAccuracy,
            metrics.MacroAccuracy,
            metrics.LogLoss);
    }

    private static int StableSeed(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var ch in value)
            {
                hash = hash * 31 + char.ToUpperInvariant(ch);
            }
            return Math.Abs(hash);
        }
    }

    private sealed class ModelState
    {
        public ModelState(MLContext mlContext) => MlContext = mlContext;
        public MLContext MlContext { get; }
        public object LoadLock { get; } = new();
        public ITransformer? Model { get; set; }
        public ObjectPool<PredictionEngine<IntentData, IntentPrediction>>? EnginePool { get; set; }
        public bool IsLoaded { get; set; }
    }
}