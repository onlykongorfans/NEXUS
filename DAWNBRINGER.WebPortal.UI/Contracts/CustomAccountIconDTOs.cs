namespace DAWNBRINGER.WebPortal.UI.Contracts;

public sealed record CustomAccountIconCollection(bool HasPendingSlot, int NextSlotID, List<CustomAccountIconSummary> Icons);
public sealed record CustomAccountIconSummary(int SlotID, string ImageURL, bool Available);
public sealed record CustomAccountIconPreview(string Image, int Width, int Height, int CropX, int CropY, int CropSize);
