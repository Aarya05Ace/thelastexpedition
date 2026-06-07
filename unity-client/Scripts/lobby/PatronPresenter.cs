// PatronPresenter.cs - THE LOST EXPEDITION lobby: The Patron (billionaire) presence (PART A + PART D).
//
// Spawns the suit (CharacterLibrary.suitPrefab) standing near the campfire FACING the lineup, seated on
// the real terrain via the shared GroundY raycast. Subscribes to the single-row Billionaire table and
// EDGE-TRIGGERS on Seq:
//   * OnInsert  -> cache lastSeq as the baseline, do NOT speak (a Patron who spoke before you joined
//                  must not pop a stale bubble).
//   * OnUpdate  -> if newRow.Seq > lastSeq: lastSeq = newRow.Seq, ALWAYS show the bubble FIRST, THEN
//                  start TTS (a TTS failure never blocks the subtitle).
// Iter().FirstOrDefault() backfills the baseline inside the lobby subscription's OnApplied.
//
// PART A: SetActive(false) keeps the suit + bubble hidden until the Lobby phase (LobbyBootstrap calls
// it). The bubble lives on the canvas ROOT (per-frame WorldToScreenPoint unaffected by layer fades) but
// its alpha is held to 0 until enabled.
//
// PART D: the bubble is a rounded BgDeep panel with an EmberDim border + a small rotated tail square
// pointing down at the suit head; PatronGold text, addressed-to-you tinted Ember + '(to you)'.

using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;

public class PatronPresenter : MonoBehaviour
{
    GameObject suit;
    Transform headAnchor;
    Camera lobbyCam;
    TtsPlayer tts;
    Vector3 lineupCenter;

    // Bubble UI (built into the bootstrap's screen-space-overlay canvas ROOT).
    RectTransform bubbleRoot;
    CanvasGroup bubbleGroup;
    Text bubbleFg;
    RectTransform bubbleTail;
    float bubbleUntil;

    uint lastSeq;
    bool primed;
    bool wired;
    bool tornDown;
    bool activated;     // PART A: false until Lobby phase

    public void Init(CharacterLibrary library, Camera cam, Canvas canvas, Vector3 suitPos, Vector3 lineup,
                     System.Func<float, float, float> groundY)
    {
        lobbyCam = cam;
        lineupCenter = lineup;
        tts = gameObject.AddComponent<TtsPlayer>();

        Vector3 seated = suitPos;
        if (groundY != null) seated.y = groundY(suitPos.x, suitPos.z);

        Vector3 toLineup = lineup - seated; toLineup.y = 0f;
        Quaternion rot = toLineup.sqrMagnitude > 0.001f ? Quaternion.LookRotation(toLineup) : Quaternion.Euler(0f, 180f, 0f);

        if (library != null && library.suitPrefab != null)
        {
            suit = Instantiate(library.suitPrefab, seated, rot);
            suit.name = "ThePatron_Suit";
        }
        else
        {
            suit = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            suit.transform.position = seated;
            suit.transform.rotation = rot;
            suit.name = "ThePatron_Suit (placeholder)";
            var col = suit.GetComponent<Collider>(); if (col) Destroy(col);
        }
        headAnchor = suit.transform;

        BuildBubble(canvas);
        SetActive(false);   // hidden until the Lobby phase
    }

    // PART A: hide/show the suit + bubble until the Lobby phase.
    public void SetActive(bool on)
    {
        activated = on;
        if (suit != null) suit.SetActive(on);
        if (!on && bubbleGroup != null) bubbleGroup.alpha = 0f;
    }

    public void WireSubscription()
    {
        if (wired) return;
        wired = true;
        var db = GameManager.Conn.Db;

        var existing = db.Billionaire.Iter().FirstOrDefault();
        if (existing != null) { lastSeq = existing.Seq; primed = true; }

        db.Billionaire.OnInsert += (ctx, row) => Baseline(row);
        db.Billionaire.OnUpdate += (ctx, oldRow, newRow) => EdgeTrigger(newRow);
    }

    void Baseline(Billionaire row)
    {
        if (!primed) { primed = true; lastSeq = row.Seq; }
    }

    void EdgeTrigger(Billionaire row)
    {
        if (!primed) { primed = true; lastSeq = row.Seq; return; }
        if (row.Seq <= lastSeq) return;
        lastSeq = row.Seq;

        bool addressedToMe = row.TargetMember.HasValue
                             && GameManager.Conn != null
                             && row.TargetMember.Value == GameManager.LocalIdentity;

        ShowBubble(row.Dialogue, addressedToMe);
        if (tts != null) tts.Speak(row.Dialogue);
    }

    void ShowBubble(string dialogue, bool addressedToMe)
    {
        if (bubbleGroup == null || !activated) return;
        string line = string.IsNullOrEmpty(dialogue) ? "..." : dialogue;
        if (addressedToMe) line = "(to you) " + line;
        // Curator presentation: an ominous quote with a quiet attribution line, no glyphs.
        string body = "\"" + line + "\"\nTHE CURATOR";
        bubbleFg.text = body;
        bubbleFg.color = addressedToMe ? LobbyUI.Ember : LobbyUI.PatronGold;
        bubbleGroup.alpha = 1f;
        bubbleUntil = Time.time + Mathf.Clamp(2.5f + body.Length * 0.06f, 4f, 12f);
    }

    void Update()
    {
        if (tornDown) return;
        if (suit != null && activated) suit.transform.Rotate(0f, 2f * Time.deltaTime, 0f);

        if (bubbleGroup == null || headAnchor == null) return;
        if (!activated) { bubbleGroup.alpha = 0f; return; }

        if (Time.time > bubbleUntil) bubbleGroup.alpha = Mathf.MoveTowards(bubbleGroup.alpha, 0f, Time.deltaTime * 2f);
        if (bubbleGroup.alpha <= 0.01f) return;

        var cam = lobbyCam;
        if (cam == null) { bubbleGroup.alpha = 0f; return; }
        Vector3 worldHead = headAnchor.position + Vector3.up * 2.4f;
        Vector3 sp = cam.WorldToScreenPoint(worldHead);
        if (sp.z <= 0f) { bubbleGroup.alpha = 0f; return; }
        bubbleRoot.position = new Vector3(sp.x, sp.y + 80f, 0f);
    }

    void BuildBubble(Canvas canvas)
    {
        if (canvas == null) return;

        // Bubble container on the canvas ROOT (positioned per-frame; unaffected by layer fades).
        var go = new GameObject("PatronBubble", typeof(RectTransform));
        go.transform.SetParent(canvas.transform, false);
        bubbleRoot = go.GetComponent<RectTransform>();
        bubbleRoot.sizeDelta = new Vector2(480f, 130f);
        bubbleGroup = go.AddComponent<CanvasGroup>();
        bubbleGroup.alpha = 0f;
        bubbleGroup.interactable = false;
        bubbleGroup.blocksRaycasts = false;

        var panel = LobbyUI.RoundedPanel(bubbleRoot, "Bubble", LobbyUI.BgDeep, 6);
        panel.rectTransform.anchorMin = Vector2.zero; panel.rectTransform.anchorMax = Vector2.one;
        panel.rectTransform.offsetMin = Vector2.zero; panel.rectTransform.offsetMax = Vector2.zero;
        LobbyUI.Border(panel.rectTransform, LobbyUI.EmberDim, 1.5f);

        // Small rotated tail square pointing down at the suit head.
        bubbleTail = LobbyUI.RoundedPanel(bubbleRoot, "Tail", LobbyUI.BgDeep, 4).rectTransform;
        LobbyUI.Place(bubbleTail, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, -6f), new Vector2(18f, 18f));
        bubbleTail.localRotation = Quaternion.Euler(0, 0, 45f);

        bubbleFg = LobbyUI.Label(bubbleRoot, "", 18, LobbyUI.PatronGold, TextAnchor.MiddleCenter);
        StorySequencer.Apply(bubbleFg, StorySequencer.Weight.Light);   // Barlow Light = ominous quote feel
        bubbleFg.horizontalOverflow = HorizontalWrapMode.Wrap;
        bubbleFg.verticalOverflow = VerticalWrapMode.Overflow;
        bubbleFg.rectTransform.anchorMin = Vector2.zero; bubbleFg.rectTransform.anchorMax = Vector2.one;
        bubbleFg.rectTransform.offsetMin = new Vector2(16f, 12f); bubbleFg.rectTransform.offsetMax = new Vector2(-16f, -12f);
    }

    public void Teardown()
    {
        tornDown = true;
        if (suit != null) Destroy(suit);
        if (bubbleRoot != null) Destroy(bubbleRoot.gameObject);
    }
}
