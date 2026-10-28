using System;
using System.Threading.Tasks;
using Mafi;
using Mafi.Localization;
using Mafi.Unity.InputControl;
using Mafi.Unity.Ui.Hud;
using Mafi.Unity.UiStatic.Toolbar;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using UnityEngine;

namespace MapShare;

// Toolbar window, in three sections: setup (create, copy and delete the link), detail (a slider
// for the most detailed zoom level) and upload (start, cancel, three progress lines). URLs are in a closed "Advanced" section.
public sealed class MapWindow : Window
{
    // Opens the window from a toolbar button.
    [GlobalDependency(RegistrationMode.AsEverything)]
    public sealed class Controller : WindowController<MapWindow>, IToolbarItemController
    {
        public bool IsVisible => true;
        public bool DeactivateShortcutsIfNotVisible => false;
        public event Action<IToolbarItemController> VisibilityChanged { add { } remove { } }

        public Controller(ControllerContext context, ToolbarHud toolbar) : base(context)
        {
            toolbar.AddMainMenuButton("Map Share".AsLoc(), this, "Assets/Unity/UserInterface/Toolbar/MapPinResource.png", 475f);
        }

        // Called every frame while the window is open. The upload changes state on other threads.
        public override bool InputUpdate()
        {
            Window.Refresh();
            return base.InputUpdate();
        }
    }

    // A second click within this time deletes the link.
    private const float CONFIRM_SECONDS = 3f;

    private readonly MapSession m_session;
    private readonly MapCapture m_capture;
    private readonly Column m_noLink;
    private readonly Column m_hasLink;
    private readonly ButtonText m_createBtn;
    private readonly ButtonText m_copyBtn;
    private readonly ButtonText m_deleteBtn;
    private readonly Label m_linkStatus;
    private readonly Slider m_detailSlider;
    private readonly Label m_detailLabel;
    private readonly ButtonText m_uploadBtn;
    private readonly Label m_uploadHint;
    private readonly Label[] m_stepLabels = new Label[3];
    private readonly Label m_unpauseHint;
    private readonly Label m_uploadStatus;
    private readonly ButtonText m_advancedBtn;
    private readonly Column m_advanced;
    private readonly TextField m_urlField;

    // Link requests run on other threads and write these.
    private volatile bool m_linkBusy;
    private volatile string m_linkMessage;

    private string m_shownLink = "";
    private bool? m_shownRunning;
    private bool? m_shownCanUpload;
    private bool? m_shownLinkBusy;
    private string m_shownLinkMessage = "";
    private string m_shownUploadStatus = "";
    private readonly MapCapture.Step[] m_shownSteps = new MapCapture.Step[3];
    private bool? m_shownHasSteps;
    private int m_shownStep;
    private int m_shownScan = -1;
    private float m_copiedUntil;
    private float m_confirmDeleteUntil;

    public MapWindow(MapSession session, MapCapture capture) : base("Map Share".AsLoc())
    {
        m_session = session;
        m_capture = capture;
        WindowSize(440.px(), Px.Auto);
        MakeMovableAndEnablePositionSaving();
        AddBodySingle(c => c.Gap(3.pt()).AlignItemsStretch(),
            // Same style as the game's WarningBanner, but its label does not wrap.
            new Row(3.pt())
            {
                new Icon("Assets/Unity/UserInterface/General/Warning128.png").Large().Color(Theme.DangerColor).NoShrink(),
                new Label(("This mod uploads pictures of your map to the internet. Anyone with the share "
                    + "link can see them.").AsLoc()).Color(Theme.DangerColor).TextOverflow(TextOverflow.Wrap),
            }.Padding(6.px(), 10.px()).Border(1.px(), Theme.DangerColor).BorderRadius(8),

            new Title("Setup".AsLoc()),
            m_noLink = new Column(2.pt())
            {
                new Label("Create a link for your map, then share it with friends.".AsLoc())
                    .TextOverflow(TextOverflow.Wrap),
                (m_createBtn = new ButtonText(Button.Primary, "Create link".AsLoc(), createLink)),
            }.AlignItemsStart(),
            m_hasLink = new Column(2.pt())
            {
                new Row(2.pt())
                {
                    (m_copyBtn = new ButtonText(Button.Primary, "Copy link".AsLoc(), copyLink)),
                    (m_deleteBtn = new ButtonText(Button.Danger, "Delete link".AsLoc(), deleteLink)
                        .Tooltip("Deletes the map from the server. The link stops working.".AsLoc())),
                },
            },
            m_linkStatus = new Label().TextOverflow(TextOverflow.Wrap),

            new Title("Detail".AsLoc()),
            new Label("Control the level of detail. Higher levels need a lot more time.".AsLoc())
                .TextOverflow(TextOverflow.Wrap),
            // One step for each zoom level. The right end is the most detail.
            m_detailSlider = new Slider().Range(0, MapCapture.LEVELS.Length - 1)
                .ValueFormatter((Func<Percent, LocStrFormatted>)(p => $"{stepAt(p) + 1}/{MapCapture.LEVELS.Length}".AsLoc()))
                .OnValueChanged(setDetail),
            m_detailLabel = new Label().TextOverflow(TextOverflow.Wrap),

            new Title("Upload".AsLoc()),
            new Label(("The mod pauses the game for the screenshot capturing, as this causes a lot of "
                + "lag. You can cancel at any time.").AsLoc()).Color(Theme.WarningColor).TextOverflow(TextOverflow.Wrap),
            new Row(2.pt())
            {
                (m_uploadBtn = new ButtonText(Button.Primary, "Upload map".AsLoc(), toggleUpload)),
                (m_uploadHint = new Label("Create a link first.".AsLoc()).TextOverflow(TextOverflow.Wrap)),
            },
            m_stepLabels[0] = new Label().TextOverflow(TextOverflow.Wrap),
            m_unpauseHint = new Label("You can unpause now. The upload continues in the background.".AsLoc())
                .TextOverflow(TextOverflow.Wrap),
            m_stepLabels[1] = new Label().TextOverflow(TextOverflow.Wrap),
            m_stepLabels[2] = new Label().TextOverflow(TextOverflow.Wrap),
            m_uploadStatus = new Label().TextOverflow(TextOverflow.Wrap),

            m_advancedBtn = new ButtonText("Advanced ▸".AsLoc(), toggleAdvanced),
            m_advanced = new Column(3.pt())
            {
                (m_urlField = new TextField().Label("Server URL".AsLoc()).Text(session.MapsUrl).OnEditEnd(setUrl)),
            });
        m_advanced.Visible(false);
        m_unpauseHint.Visible(false);
        int step = MapCapture.LEVELS.Length - 1 - Math.Max(0, Array.IndexOf(MapCapture.LEVELS, session.FinestLevel));
        m_detailSlider.Value(step);
        showDetail(step);
    }

    private static int stepAt(Percent p) => (int)Math.Round(p.ToFloat() * (MapCapture.LEVELS.Length - 1));

    // Snaps the slider to a step. A change applies to the next upload.
    private void setDetail(float oldValue, float newValue)
    {
        int step = (int)Math.Round(newValue);
        m_detailSlider.Value(step);
        m_session.SetFinestLevel(MapCapture.LEVELS[MapCapture.LEVELS.Length - 1 - step]);
        showDetail(step);
    }

    private void showDetail(int step)
    {
        m_shownStep = step;
        m_shownScan = m_capture.ScanCount;
        int z = MapCapture.LEVELS[MapCapture.LEVELS.Length - 1 - step];
        double mb = (double)m_capture.PictureCount(z, out int sparse, out int sparseFull) * MapCapture.AVG_PICTURE_BYTES / 1e6;
        string size = mb >= 1000 ? $"{mb / 1000:0.0} GB" : $"{Math.Max(1, mb):0} MB";
        string text = $"Upload size: ca. {size}.";
        if (z < MapCapture.SPARSE_BELOW)
        {
            // Slider steps count from 1.
            int full = MapCapture.LEVELS.Length - Array.IndexOf(MapCapture.LEVELS, MapCapture.SPARSE_BELOW);
            text = "For performance reasons, this detail level is only captured for areas with buildings, vehicles "
                + $"or mines, and uses level {full} for all other areas ({100 * (sparseFull - sparse) / sparseFull} % fewer screenshots)." + text;
        }
        m_detailLabel.Value(text.AsLoc());
    }

    private void createLink()
    {
        _ = runLinkAsync(m_session.CreateLinkAsync, "Creating the link.", "Cannot create the link: ");
    }

    // The first click asks for a second click. An upload that runs stops first.
    private void deleteLink()
    {
        if (Time.unscaledTime > m_confirmDeleteUntil)
        {
            m_confirmDeleteUntil = Time.unscaledTime + CONFIRM_SECONDS;
            m_deleteBtn.Value("Click again to delete".AsLoc());
            return;
        }
        m_confirmDeleteUntil = 0;
        m_deleteBtn.Value("Delete link".AsLoc());
        m_capture.Cancel();
        _ = runLinkAsync(m_session.DeleteLinkAsync, "Deleting the map from the server.", "Cannot delete the map: ");
    }

    private async Task runLinkAsync(Func<Task> request, string busy, string error)
    {
        if (m_linkBusy) return;
        m_linkBusy = true;
        m_linkMessage = busy;
        try
        {
            await request();
            m_linkMessage = null;
        }
        catch (Exception e)
        {
            Log.Warning("Map Share: " + error + e.Message);
            m_linkMessage = error + e.Message;
        }
        finally
        {
            m_linkBusy = false;
        }
    }

    private void toggleUpload()
    {
        if (m_capture.Running) m_capture.Cancel();
        else m_capture.Start();
    }

    private void copyLink()
    {
        string link = m_session.ShareLink;
        if (link == null) return;
        GUIUtility.systemCopyBuffer = link;
        m_copyBtn.Value("Link copied".AsLoc());
        m_copiedUntil = Time.unscaledTime + 2f;
    }

    private void toggleAdvanced()
    {
        bool open = !m_advanced.IsVisible();
        m_advanced.Visible(open);
        m_advancedBtn.Value((open ? "Advanced ▾" : "Advanced ▸").AsLoc());
    }

    // An invalid URL falls back to the default. The field shows the value that is used.
    private void setUrl(string text)
    {
        m_session.SetMapsUrl(text);
        m_urlField.Text(m_session.MapsUrl);
    }

    // Updates the components only when something changed.
    public void Refresh()
    {
        float now = Time.unscaledTime;
        if (m_copiedUntil > 0 && now > m_copiedUntil)
        {
            m_copiedUntil = 0;
            m_copyBtn.Value("Copy link".AsLoc());
        }
        if (m_confirmDeleteUntil > 0 && now > m_confirmDeleteUntil)
        {
            m_confirmDeleteUntil = 0;
            m_deleteBtn.Value("Delete link".AsLoc());
        }

        // Map link.
        string link = m_session.ShareLink;
        if (link != m_shownLink)
        {
            m_shownLink = link;
            m_noLink.Visible(link == null);
            m_hasLink.Visible(link != null);
        }
        bool linkBusy = m_linkBusy;
        if (linkBusy != m_shownLinkBusy)
        {
            m_shownLinkBusy = linkBusy;
            m_createBtn.Enabled(!linkBusy);
            m_deleteBtn.Enabled(!linkBusy);
        }
        showText(m_linkStatus, m_linkMessage, ref m_shownLinkMessage);

        // Upload.
        bool running = m_capture.Running;
        // The player builds while the window is open.
        if (!running) m_capture.RefreshScan();
        if (m_capture.ScanCount != m_shownScan) showDetail(m_shownStep);
        if (running != m_shownRunning)
        {
            m_shownRunning = running;
            m_uploadBtn.Value((running ? "Cancel upload" : "Upload map").AsLoc());
            // A change would apply only to the next upload.
            m_detailSlider.Enabled(!running);
        }
        // A delete must not race with a new upload.
        bool canUpload = running || (link != null && !linkBusy);
        if (canUpload != m_shownCanUpload)
        {
            m_shownCanUpload = canUpload;
            m_uploadBtn.Enabled(canUpload);
        }
        if (link == null != m_uploadHint.IsVisible()) m_uploadHint.Visible(link == null);
        MapCapture.Step[] steps = m_capture.Steps;
        bool hasSteps = steps != null;
        if (hasSteps != m_shownHasSteps)
        {
            m_shownHasSteps = hasSteps;
            foreach (Label label in m_stepLabels) label.Visible(hasSteps);
        }
        for (int i = 0; hasSteps && i < steps.Length; i++)
        {
            MapCapture.Step step = steps[i];
            if (step.Text == m_shownSteps[i].Text && step.State == m_shownSteps[i].State) continue;
            m_shownSteps[i] = step;
            m_stepLabels[i].Value(step.Text.AsLoc()).Color(stepColor(step.State));
        }
        // The capture is done, but the upload still runs.
        bool unpause = running && hasSteps && steps[0].State == MapCapture.StepState.Done;
        if (unpause != m_unpauseHint.IsVisible()) m_unpauseHint.Visible(unpause);
        showText(m_uploadStatus, m_capture.Message, ref m_shownUploadStatus);
    }

    private static ColorRgba stepColor(MapCapture.StepState state) => state switch
    {
        MapCapture.StepState.Done => Theme.PositiveColor,
        MapCapture.StepState.Failed => Theme.DangerColor,
        MapCapture.StepState.Running => Theme.DefaultColor,
        _ => Theme.InactiveColor,
    };

    // Hides the label for null.
    private static void showText(Label label, string text, ref string shown)
    {
        if (text == shown) return;
        shown = text;
        label.Visible(text != null);
        if (text != null) label.Value(text.AsLoc());
    }

    // Estimate only.
    public static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{Math.Max(1, seconds)} s";
        if (seconds < 3600) return $"{(seconds + 30) / 60} min";
        long minutes = (seconds + 30) / 60;
        return $"{minutes / 60} h {minutes % 60} min";
    }
}
