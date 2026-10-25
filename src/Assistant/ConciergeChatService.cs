using larp.Prediction;
using System.Threading;
using System.Threading.Tasks;

namespace larp.Assistant;

public interface IConciergeChatService
{
    Task<string> GetResponseAsync(string message, OutagePrediction? prediction, CancellationToken cancellationToken = default);
}
