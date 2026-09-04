namespace MoTask.Core.Abstractions;

public sealed class PersistenceException : Exception
{
    public PersistenceException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
