namespace MERRICK.DatabaseContext.Handlers;

public static class CustomAccountIcons
{
    public const int ProductID = 464;
    public const string PendingCode = "ai.custom_icon";
    public const string SlotPrefix = "ai.custom_icon:";

    public static string GetCode(int slotID) => SlotPrefix + slotID.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static int? GetSlotID(string code)
        => code.StartsWith(SlotPrefix, StringComparison.Ordinal)
            && int.TryParse(code.AsSpan(SlotPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int slotID)
            && slotID > 0 ? slotID : null;

    public static List<int> GetSlots(User user)
        => user.OwnedStoreItems.Select(GetSlotID).OfType<int>().Distinct().Order().ToList();

    public static int GetNextSlot(User user) => checked(GetSlots(user).DefaultIfEmpty(0).Max() + 1);
}
