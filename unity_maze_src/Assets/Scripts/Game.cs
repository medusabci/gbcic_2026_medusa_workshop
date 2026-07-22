using Cinemachine;
using System;
using System.Linq;
using TMPro;
using UnityEngine;

// Central maze/game orchestrator: procedural generation, player movement and win condition.
// Lives on its own "GameManager" GameObject (not on a camera) so a future input layer
// (e.g. a BCI control scheme) has a single, obvious place to hook into.
// Runs in Edit Mode too (ExecuteAlways) so the maze can be previewed before pressing Play;
// edit-mode-only preview objects are marked DontSaveInEditor so they never pollute the scene file.
[Serializable]
public struct ColorSet
{
    public Color backgroundColor, primaryColor, secondaryColor;
}

[ExecuteAlways]
public class Game : MonoBehaviour
{
    const int MaxSize = 16;

    public float holep;
    public int w, h, x, y;
    public int level = 1;
    public bool[,] hwalls, vwalls;
    public Transform Level, Player, Goal;
    public GameObject Floor, Wall;

    [Header("Cameras")]
    public CinemachineVirtualCamera cam; // player-follow vcam, temporarily disabled (see Generate)
    public Camera mainCamera; // fixed camera framing the whole maze

    [Header("HUD")]
    public TextMeshProUGUI levelText, timeText;

    [Header("Colors (cycles every level)")]
    public ColorSet[] colorSets =
    {
        new() { backgroundColor = new Color32(0xEA, 0xE6, 0xDF, 0xFF), primaryColor = new Color32(0xE6, 0xDD, 0xD2, 0xFF), secondaryColor = new Color32(0xC5, 0x84, 0x1D, 0xFF) },
        new() { backgroundColor = new Color32(0xE3, 0xF0, 0xF5, 0xFF), primaryColor = new Color32(0xF5, 0xFB, 0xFC, 0xFF), secondaryColor = new Color32(0x2E, 0x7D, 0x9E, 0xFF) },
        new() { backgroundColor = new Color32(0xEA, 0xF2, 0xE4, 0xFF), primaryColor = new Color32(0xF3, 0xF8, 0xEE, 0xFF), secondaryColor = new Color32(0x4C, 0x8C, 0x3C, 0xFF) },
        new() { backgroundColor = new Color32(0xEF, 0xE6, 0xF5, 0xFF), primaryColor = new Color32(0xF8, 0xF2, 0xFB, 0xFF), secondaryColor = new Color32(0x7A, 0x4F, 0xB0, 0xFF) },
        new() { backgroundColor = new Color32(0xF5, 0xE6, 0xE3, 0xFF), primaryColor = new Color32(0xFB, 0xF2, 0xF0, 0xFF), secondaryColor = new Color32(0xC2, 0x50, 0x3F, 0xFF) },
    };

    int goalX, goalY;
    static readonly (int dx, int dy)[] Steps = { (-1, 0), (1, 0), (0, -1), (0, 1) };
    static readonly KeyCode[][] Keys =
    {
        new[] { KeyCode.A, KeyCode.LeftArrow },
        new[] { KeyCode.D, KeyCode.RightArrow },
        new[] { KeyCode.S, KeyCode.DownArrow },
        new[] { KeyCode.W, KeyCode.UpArrow },
    };

    void Start() => Generate();

    [ContextMenu("Generate Maze")]
    void Generate()
    {
        if (!Level || !Player || !Goal || !Floor || !Wall) return;

        foreach (Transform child in Level)
            if (Application.isPlaying) Destroy(child.gameObject); else DestroyImmediate(child.gameObject);

        hwalls = new bool[w + 1, h];
        vwalls = new bool[w, h + 1];
        var st = new int[w, h];

        void dfs(int x, int y)
        {
            st[x, y] = 1;

            var dirs = new[]
            {
                (x - 1, y, hwalls, x, y, Vector3.right, 90),
                (x + 1, y, hwalls, x + 1, y, Vector3.right, 90),
                (x, y - 1, vwalls, x, y, Vector3.up, 0),
                (x, y + 1, vwalls, x, y + 1, Vector3.up, 0),
            };
            foreach (var (nx, ny, wall, wx, wy, sh, ang) in dirs.OrderBy(d => UnityEngine.Random.value))
                if (!(0 <= nx && nx < w && 0 <= ny && ny < h) || (st[nx, ny] == 2 && UnityEngine.Random.value > holep))
                {
                    wall[wx, wy] = true;
                    Spawn(Wall, new Vector3(wx, wy) - sh / 2, Quaternion.Euler(0, 0, ang));
                }
                else if (st[nx, ny] == 0) dfs(nx, ny);
            st[x, y] = 2;
        }
        dfs(0, 0);
        SpawnFloor(); // dfs visits every cell, so the floor is always the full w x h rectangle

        x = UnityEngine.Random.Range(0, w);
        y = UnityEngine.Random.Range(0, h);
        Player.position = new Vector3(x, y);
        do Goal.position = new Vector3(UnityEngine.Random.Range(0, w), UnityEngine.Random.Range(0, h));
        while (Vector3.Distance(Player.position, Goal.position) < (w + h) / 4);
        goalX = (int)Goal.position.x;
        goalY = (int)Goal.position.y;

        if (cam) cam.gameObject.SetActive(false);
        FrameMaze();
        ApplyColors();

        if (levelText) levelText.text = $"Nivel {level}";
        if (timeText) timeText.text = "00:00";
    }

    // Colors may change between levels, so every generation re-applies them to everything
    // currently in the scene; OnValidate also calls this for instant feedback while editing.
    void ApplyColors()
    {
        if (colorSets == null || colorSets.Length == 0) return;
        var c = colorSets[(level - 1) % colorSets.Length];

        if (mainCamera) mainCamera.backgroundColor = c.backgroundColor;
        if (Player) Player.GetComponent<SpriteRenderer>().color = c.primaryColor;
        if (Goal) Goal.GetComponent<SpriteRenderer>().color = c.primaryColor;

        var trail = Player ? Player.GetComponent<TrailRenderer>() : null;
        if (trail)
        {
            var colorKeys = trail.colorGradient.colorKeys;
            for (int i = 0; i < colorKeys.Length; i++) colorKeys[i].color = c.primaryColor;
            var gradient = new Gradient();
            gradient.SetKeys(colorKeys, trail.colorGradient.alphaKeys);
            trail.colorGradient = gradient;
        }

        if (Level)
            foreach (Transform child in Level)
            {
                var sr = child.GetComponent<SpriteRenderer>();
                if (sr) sr.color = child.name.StartsWith("Floor") ? c.secondaryColor : c.primaryColor;
            }
    }

    void OnValidate() => ApplyColors();

    // Fixed camera centered on the current maze, zoomed to fit its width/height.
    void FrameMaze()
    {
        if (!mainCamera) return;
        mainCamera.transform.position = new Vector3((w - 1) / 2f, (h - 1) / 2f, -10f);
        const float pad = 1.5f;
        float vertical = h / 2f + pad;
        float horizontal = (w / 2f + pad) / Mathf.Max(mainCamera.aspect, 0.01f);
        mainCamera.orthographicSize = Mathf.Max(vertical, horizontal);
    }

    // Single tiled sprite covering the whole maze rectangle, instead of one tile per cell:
    // avoids per-tile seams and the GameObject/draw-call overhead of up to MaxSize^2 instances.
    void SpawnFloor()
    {
        var floor = Spawn(Floor, new Vector3((w - 1) / 2f, (h - 1) / 2f, 0), Quaternion.identity);
        var sr = floor.GetComponent<SpriteRenderer>();
        sr.drawMode = SpriteDrawMode.Tiled;
        sr.size = new Vector2(w, h);
        // Switching drawMode/size can make Unity auto-compensate the transform scale
        // to preserve the sprite's old apparent size; force it back to 1 afterwards.
        floor.transform.localScale = Vector3.one;
    }

    GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        var go = Instantiate(prefab, position, rotation, Level);
        if (!Application.isPlaying) go.hideFlags = HideFlags.DontSaveInEditor;
        return go;
    }

    bool Blocked(int dir, int cx, int cy) => dir switch
    {
        0 => hwalls[cx, cy],
        1 => hwalls[cx + 1, cy],
        2 => vwalls[cx, cy],
        _ => vwalls[cx, cy + 1],
    };

    void Update()
    {
        if (!Application.isPlaying) return;

        for (int dir = 0; dir < 4; dir++)
            if (Keys[dir].Any(Input.GetKeyDown))
            {
                var (dx, dy) = Steps[dir];
                int nx = x, ny = y;
                bool moved = false;
                while (!Blocked(dir, nx, ny))
                {
                    nx += dx; ny += dy;
                    moved = true;
                    if (nx == goalX && ny == goalY) break; // always stop on the goal cell

                    int perpA = dir < 2 ? 2 : 0, perpB = dir < 2 ? 3 : 1;
                    if (!Blocked(perpA, nx, ny) || !Blocked(perpB, nx, ny)) break; // stop at the next intersection
                }
                if (!moved)
                    Player.position = Vector3.Lerp(Player.position, new Vector3(x + dx, y + dy), 0.1f);
                (x, y) = (nx, ny);
            }

        Player.position = Vector3.Lerp(Player.position, new Vector3(x, y), Time.deltaTime * 12);
        if (timeText) timeText.text = TimeSpan.FromSeconds(Time.time).ToString(@"mm\:ss");

        if (Vector3.Distance(Player.position, Goal.position) < 0.12f)
        {
            level++;
            if (UnityEngine.Random.Range(0, 5) < 3) w = Mathf.Min(w + 1, MaxSize);
            else h = Mathf.Min(h + 1, MaxSize);
            Generate();
        }
    }
}
