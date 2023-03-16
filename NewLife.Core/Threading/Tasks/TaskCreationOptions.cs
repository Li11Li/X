namespace System.Threading.Tasks;

[Serializable]
[Flags]
public enum TaskCreationOptions
{
	None = 0,
	PreferFairness = 1,
	LongRunning = 2,
	AttachedToParent = 4,
	DenyChildAttach = 8,
	HideScheduler = 0x10
}
