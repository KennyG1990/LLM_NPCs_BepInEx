using System;
using System.Collections.Generic;
using FoxyVoxel.Logging;
using NSEipix.Base;
using NSMedieval.Dialogs.Data;
using UnityEngine;

namespace NSMedieval.Dialogs;

public class DialogViewManager : MonoSingleton<DialogViewManager>
{
	private int selectedOptionIndex;

	[SerializeField]
	private DialogView view;

	private readonly List<GameObject> tempAdditionalViews = new List<GameObject>();

	public event Action<int> OnClose;

	public event Action<int> OnCloseAfterAnimation;

	public void InvokeOnCloseEvent(int selectedOptionIndex)
	{
		this.OnClose?.Invoke(selectedOptionIndex);
	}

	public void OpenDialog(DialogContent dialogContent, bool appendCloseToButtons = true)
	{
		if (appendCloseToButtons)
		{
			for (int i = 0; i < dialogContent.Options.Count; i++)
			{
				DialogOption dialogOption = dialogContent.Options[i];
				int indexClosureCopy = i;
				dialogOption.OnSelected = (Action)Delegate.Combine(dialogOption.OnSelected, (Action)delegate
				{
					for (int j = 0; j < dialogContent.Options.Count; j++)
					{
						if (dialogContent.Options[j] != null)
						{
							dialogContent.Options[j].OnSelected = null;
						}
					}
					Close(indexClosureCopy);
				});
			}
		}
		view.Open(dialogContent);
	}

	public void OpenDialog(string text, string title = "")
	{
		DialogContent dialogContent = new DialogContent();
		dialogContent.ContentBodyText = text;
		dialogContent.WindowTitle = title;
		OpenDialog(dialogContent);
	}

	public void Close(int selectedOptionIndex)
	{
		this.selectedOptionIndex = selectedOptionIndex;
		view.Close(OnClosedAfterAnimation);
		InvokeOnCloseEvent(selectedOptionIndex);
		ClearAdditionalTempViews();
	}

	private void OnClosedAfterAnimation()
	{
		this.OnCloseAfterAnimation?.Invoke(selectedOptionIndex);
	}

	public void CloseSilent()
	{
		view.Close();
		ClearAdditionalTempViews();
	}

	public void AddTempAdditionalView(Canvas canvas)
	{
		if (canvas == null)
		{
			Log.Error("Failed to add temp additional view clone: it is null", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\UI\\View\\Dialog\\DialogViewManager.cs");
			return;
		}
		canvas.sortingOrder = view.GetComponent<Canvas>().sortingOrder + 1;
		tempAdditionalViews.Add(canvas.gameObject);
	}

	private void ClearAdditionalTempViews()
	{
		foreach (GameObject tempAdditionalView in tempAdditionalViews)
		{
			UnityEngine.Object.Destroy(tempAdditionalView);
		}
		tempAdditionalViews.Clear();
	}

	protected override void OnDestroy()
	{
		this.OnClose = null;
		this.OnCloseAfterAnimation = null;
		base.OnDestroy();
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
