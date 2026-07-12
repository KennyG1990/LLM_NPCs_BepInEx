using System;
using System.Collections.Generic;
using NSEipix.Base;
using NSMedieval.Controllers;
using NSMedieval.Serialization;

namespace NSMedieval.Dialogs.Data;

[Serializable]
[FVSerializableKey("DialogContent", "")]
public class DialogContent : IFVSerializable
{
	public string WindowTitle = "";

	public string ContentTitle = "";

	public string ContentBodyText = "";

	public string ContentBodyImagePath = "";

	public List<DialogOption> Options = new List<DialogOption>();

	public bool ShowCloseButton;

	private const string fvs_WindowTitle = "WindowTitle";

	private const string fvs_ContentTitle = "ContentTitle";

	private const string fvs_ContentBodyText = "ContentBodyText";

	private const string fvs_ContentBodyImagePath = "ContentBodyImagePath";

	private const string fvs_Options = "Options";

	private const string fvs_ShowCloseButton = "ShowCloseButton";

	public DialogContent()
	{
	}

	public void Localize()
	{
		Format(_Localize, skipImage: true);
		static string _Localize(string locKey)
		{
			return MonoSingleton<LocalizationController>.Instance.GetText(locKey, BodyType.None);
		}
	}

	public void Replace(string src, string dest)
	{
		Format(_Replace);
		string _Replace(string text)
		{
			return text.Replace(src, dest);
		}
	}

	public void Format(Func<string, string> formatFunction, bool skipImage = false)
	{
		WindowTitle = nullCheckedFormatFunc(WindowTitle);
		ContentTitle = nullCheckedFormatFunc(ContentTitle);
		ContentBodyText = nullCheckedFormatFunc(ContentBodyText);
		if (!skipImage)
		{
			ContentBodyImagePath = nullCheckedFormatFunc(ContentBodyImagePath);
		}
		foreach (DialogOption option in Options)
		{
			option.Text = nullCheckedFormatFunc(option.Text);
			option.DisabledTooltip = nullCheckedFormatFunc(option.DisabledTooltip);
			foreach (TooltipData tooltip in option.Tooltips)
			{
				tooltip.Key = nullCheckedFormatFunc(tooltip.Key);
				for (int i = 0; i < tooltip.Args.Count; i++)
				{
					tooltip.Args[i] = nullCheckedFormatFunc(tooltip.Args[i]);
				}
			}
		}
		string nullCheckedFormatFunc(string text)
		{
			if (text != null)
			{
				return formatFunction(text);
			}
			return null;
		}
	}

	public void Serialize(FVSerializer serializer)
	{
		serializer.Write("WindowTitle", WindowTitle);
		serializer.Write("ContentTitle", ContentTitle);
		serializer.Write("ContentBodyText", ContentBodyText);
		serializer.Write("ContentBodyImagePath", ContentBodyImagePath);
		serializer.Write("Options", Options);
		serializer.Write("ShowCloseButton", ShowCloseButton);
	}

	public DialogContent(FVDeserializer deserializer)
	{
		WindowTitle = deserializer.ReadString("WindowTitle");
		ContentTitle = deserializer.ReadString("ContentTitle");
		ContentBodyText = deserializer.ReadString("ContentBodyText");
		ContentBodyImagePath = deserializer.ReadString("ContentBodyImagePath");
		Options = deserializer.ReadObjectList<DialogOption>("Options");
		ShowCloseButton = deserializer.ReadBool("ShowCloseButton");
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
