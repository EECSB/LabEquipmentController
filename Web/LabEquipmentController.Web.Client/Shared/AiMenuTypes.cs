namespace LabEquipmentController.Web.Client.Shared;

/// <summary>A saved model as <see cref="AiModelMenu"/> lists it: its name, and its provider beside that.</summary>
public sealed record AiMenuModel(string Id, string Name, string Provider);

/// <summary>An effort as <see cref="AiModelMenu"/> offers it: "" for Default.</summary>
public sealed record AiMenuLevel(string Value, string Label);

/// <summary>One entry of a <see cref="SavedPicker"/>: its key and the name it is shown by.</summary>
public sealed record SavedPickerItem(string Key, string Name);
