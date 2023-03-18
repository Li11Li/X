namespace System.Threading.Tasks;

[Serializable]
[Flags]
public enum TaskContinuationOptions
{
	None = 0,
	PreferFairness = 1,
	LongRunning = 2,
	AttachedToParent = 4,
	NotOnRanToCompletion = 0x10000,
	NotOnFaulted = 0x20000,
	NotOnCanceled = 0x40000,
	OnlyOnRanToCompletion = 0x60000,
	OnlyOnFaulted = 0x50000,
	OnlyOnCanceled = 0x30000,
	ExecuteSynchronously = 0x80000
}