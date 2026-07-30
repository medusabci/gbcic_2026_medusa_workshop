// MEDUSA-PLATFORM 
// v2025 RHEA
// www.medusabci.com

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;


public class Manager : MonoBehaviour
{
    // Public parameters
    public string IP = "127.0.0.1";
    public int port = 50000;

    public float fpsResolution = 60;    // Screen refresh rate (frequency bin, resolution) in Hz
    public int nCmmds = 4;
    public int testCycles = 1;
    private List<string> testTarget;

    public float tPrevText = 1.00f;
    public float tPrevIddle = 1.00f;
    public float tFinishText = 1.00f;

    private MessageInterpreter.ParameterDecoder parameters = null;

    // MEDUSA RUN STATES
    const int RUN_STATE_READY = 0;           // READY
    const int RUN_STATE_RUNNING = 1;         // RUNNING
    const int RUN_STATE_PAUSED = 2;          // PAUSED
    const int RUN_STATE_STOP = 3;            // TRANSITORY STATE WHILE USER PRESS THE STOP BUTTON AND MEDUSA IS READY TO START A NEW RUN AGAIN
    const int RUN_STATE_FINISHED = 4;        // THE RUN IS STILL ACTIVE, BUT FINISHED

    // Inner states
    const int STATE_WAITING_CONNECTION = -2;    // states of "state"
    const int STATE_WAITING_PARAMS = -1;

    const int STATE_WAITING_SELECTION = 18;
    const int STATE_SELECTION_RECEIVED = 19;
    const int STATE_SELECTION_IDDLE = 20;

    const int STATE_RUNNING_PREVTEXT = 10;      // states of "innerstate" of innerRunningCycle()
    const int STATE_RUNNING_IDDLE = 11;
    const int STATE_RUNNING_TARGET = 12;
    const int STATE_RUNNING_IDDLE2 = 13;
    const int STATE_RUNNING_FLICKERING = 14;

    const int STATE_FINISHING_IDDLE = 25;       // states of "finishingstate" of finishingCycle()
    const int STATE_FINISHING_TEXT = 26;

    const int STATE_CLOSING_TEXT = 27;          // states of "closingstate" of closingApplication()
    const int STATE_CLOSING_FINAL = 28;
    const int STATE_EVERYTHING_CLOSED = 29;

    const int STATE_TRANSITION_TEXT = 30;       // states of "transitionstate" of transitionFastMode()
    const int STATE_TRANSITION_IDDLE = 31;

    const int STATE_RESULT_SHOW = 50;           // states of "resultstate" of showingResult()
    const int STATE_RESULT_IDDLE = 51;
    const int STATE_RESULT_END = 52;

    // State controllers and coroutines
    static int state = STATE_WAITING_CONNECTION;
    static int innerstate = STATE_RUNNING_PREVTEXT;
    static int finishingstate = STATE_FINISHING_IDDLE;
    static int closingstate = STATE_CLOSING_TEXT;
    static int resultstate = STATE_RESULT_SHOW;
    static bool mustStartTrial = false;
    static bool mustFinishRun = false;
    static bool mustClose = false;
    static bool mustShowResult = false;

    // Colors
    public Color32 defaultBoxColor = Color.gray;
    public Color32 highlightResultBoxColor = Color.green;


    // FPS counter
    private float updateCount = 0;
    private float fixedUpdateCount = 0;
    private float updateUpdateCountPerSecond;
    private float updateFixedUpdateCountPerSecond;

    // Matrices
    public GameObject matrixObject;
    private List<int[]> matrixItemSequence; 
    private int matrixCurrentTimeShift;
    private MessageInterpreter.ParameterDecoder.Matrix matrix;
    private int matrixSequenceLength;

    // Other attributes
    private Vector2 lastScreenSize;
    static int currentTestTarget = 0;
    private MessageInterpreter messageInterpreter = new MessageInterpreter();
    private Camera mainCamera;
    private Canvas mainCanvas;
    private GameObject fpsMonitorText, informationBox, informationText, debugText, mainCell, resultBox, resultText, photodiodeCell, blinkIcon;
    private float cellSize;
    private float width, height;
    private bool targetsAvailable;
    private int cycleTestCounter = 0;
    private string lastResultUid = "";

    // TCP client
    private MedusaTCPClient tcpClient;

    // Required for raster latencies (only works for Windows)
    [DllImport("user32.dll", EntryPoint = "FindWindow")]
    public static extern IntPtr FindWindow(System.String className, System.String windowName);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    public static extern bool GetWindowRect(IntPtr hwnd, ref Rect rectangle);
    public int lastWindowLeft = 0;
    public int lastWindowTop = 0;

    // Game
    public Game gameManager;

    /* ----------------------------------- GUI HELPERS  ------------------------------------ */

    private void changeItemColor(GameObject item, Color32 color)
    {
        item.transform.GetChild(0).GetComponent<Image>().color = color;
    }

    /* ----------------------------------- UNITY LIFE-CYCLE FUNCTIONS ------------------------------------ */

    void Awake()
    {
        lastScreenSize = new Vector2(Screen.width, Screen.height);

        if (!Application.isEditor)
        {
            // Take the IP and port from the arguments
            // Usage: c-VEP Speller.exe 127.0.0.1 50000
            string[] arguments = Environment.GetCommandLineArgs();
            IP = arguments[1];
            port = Int32.Parse(arguments[2]);
        }
            
    }

    // Start is called before the first frame update
    void Start()
    {
        // Start the TCP/IP server
        tcpClient = new MedusaTCPClient(this, IPAddress.Parse(IP), port);
        tcpClient.Start();

        // Find Matrix
        matrixObject = GameObject.Find("Matrix");

        // FPS monitoring
        fpsMonitorText = GameObject.Find("FPSmonitor");
        StartCoroutine(monitorFPS());

        // Information text and box
        informationBox = GameObject.Find("InformationBox");
        informationText = GameObject.Find("InformationText");


        // WAIT until parameters are received!
        state = STATE_WAITING_CONNECTION;
    }

    // Show the FPS monitoring: current FPS and refresh rate
    void OnGUI()
    {
        fpsMonitorText.GetComponent<Text>().text = updateUpdateCountPerSecond.ToString() + " fps (@" + updateFixedUpdateCountPerSecond.ToString() + ")";
        if (updateFixedUpdateCountPerSecond < fpsResolution)
        {
            fpsMonitorText.GetComponent<Text>().color = Color.red; 
        }
        else
        {
            fpsMonitorText.GetComponent<Text>().color = Color.green;
        }
    }

    // This function quits the current application by stopping the TCP client and closing the window
    public void quitApplication()
    {
        if (tcpClient.socketConnection != null)
        {
            bool tcpClosed = tcpClient.Stop();
            if (tcpClosed)
            {
                Debug.Log("> MedusaTCPClient closed successfully!");
            }
        }
        Application.Quit();
    }

    public void quitApplicationFromException()
    {
        mustClose = true;
    }


    /* ------------------------------------- UPDATE FUNCTIONS -------------------------------------- */

    // Update call (FPS may vary)
    void Update()
    {
        updateCount += 1;

        /* MINIMUM RESOLUTION */
        if (Screen.width < 450 || Screen.height < 450)
        {
            Screen.SetResolution(450, 450, false);
        }

        /* BEHAVIOR FOR DIFFERENT STATES */
        // If the TCP client just connected, request the parameters
        if (state == STATE_WAITING_CONNECTION && tcpClient.isConnected())
        {
            state = STATE_WAITING_PARAMS;
            // If the connection have been just established, send the waiting flag
            ServerMessage sm = new ServerMessage("waiting");
            tcpClient.SendMessage(sm.ToJson());
        }

        // If we are waiting the parameters
        if (state == STATE_WAITING_PARAMS)
        {
            // If parameters have been already received, execute this in the main thread
            if (parameters != null)
            {
                onParametersReady();
            }
        }

        // If we have received a new selection, show it in the main thread
        if (state == STATE_SELECTION_RECEIVED)
        {
            if (resultstate == STATE_RESULT_SHOW)
            {
                // Show the result
                if (!String.IsNullOrEmpty(lastResultUid))
                {
                    GameObject cell = matrixObject.transform.Find(lastResultUid).gameObject;
                    changeItemColor(cell, highlightResultBoxColor);
                }
            }

            if (resultstate == STATE_RESULT_IDDLE)
            {
                // Default color
                GameObject cell = matrixObject.transform.Find(lastResultUid).gameObject;
                changeItemColor(cell, defaultBoxColor);
            }

            if (resultstate == STATE_RESULT_END)
            {
                // Start another trial?
                if (targetsAvailable)
                {
                    if (currentTestTarget >= testTarget.Count)
                    {
                        // If all the targets have been done, notify the server to finish the app
                        mustFinishRun = true;
                        state = RUN_STATE_FINISHED;
                    }
                }
                if (state != RUN_STATE_FINISHED)
                {
                    // Starting another trial
                    state = RUN_STATE_RUNNING;
                    innerstate = STATE_RUNNING_IDDLE;
                    mustStartTrial = true;
                }

                // Reset result
                lastResultUid = "";
                resultstate = STATE_RESULT_SHOW;
            }

        }

        // If the run is finished
        if (state == RUN_STATE_FINISHED)
        {
            if (finishingstate == STATE_FINISHING_IDDLE)
            {
                setInformationText("");
            }
            if (finishingstate == STATE_FINISHING_TEXT)
            {
                setInformationText("Run finished");
            }
        }

        // If the Unity app is stopping (closing)
        if (state == RUN_STATE_STOP)
        {
            if (closingstate == STATE_CLOSING_TEXT)
            {
                setInformationText("Closing...");
            }
            if (closingstate == STATE_CLOSING_FINAL)
            {
                quitApplication();
                closingstate = STATE_EVERYTHING_CLOSED;
            }
        }

        /* EXECUTING CO-ROUTINES*/

        // If a new trial should be started, run the co-routine
        if (mustStartTrial)
        {
            mustStartTrial = false;
            StartCoroutine(innerRunningCycle());
        }

        // If we must show the received result
        if (mustShowResult)
        {
            mustShowResult = false;
            StartCoroutine(showingResult());
            if (Enum.TryParse<Direction>(lastResultUid, out Direction dir))
            {
                gameManager.Move(dir);
            }
        }

        // If the run must finish
        if (mustFinishRun)
        {
            // Show the finished text and notify
            mustFinishRun = false;
            StartCoroutine(finishingCycle());
        }

        // If the TCPServer must close
        if (mustClose)
        {
            if (tcpClient.socketConnection != null) 
            { 
                // Send the confirmation that the Unity's client is going to close
                ServerMessage sm = new ServerMessage("close");
                tcpClient.SendMessage(sm.ToJson());
            }

            // Close the application
            mustClose = false;         // Avoid sending it twice
            StartCoroutine(closingApplication());
        }
    }

    // Fixed update at fpsResolution (setted before in onParametersReady())
    void FixedUpdate()
    {
        fixedUpdateCount += 1;

        // Mode
        if (state == RUN_STATE_RUNNING)
        {
            loopTest();
        }
    }

    // This function resets the matrix by unflashing everything
    void resetMatrix()
    {
        matrixCurrentTimeShift = 0;
        for (int i = 0; i < nCmmds; i++)
        {
            GameObject cell = matrixObject.transform.GetChild(i).gameObject;
            changeItemColor(cell, defaultBoxColor);   
        }
    } 

    // This function sets the information text. IMPORTANT: only the main thread is allowed to run this function.
    void setInformationText(string infoMsg)
    {
        if (string.IsNullOrEmpty(infoMsg))
        {
            informationBox.SetActive(false);
            informationText.SetActive(false);
        }
        else
        {
            informationBox.SetActive(true);
            informationText.SetActive(true);
            informationText.GetComponent<Text>().text = infoMsg;
        }
    }

    // This function controls the visibility of the matrix
    void setMatrixVisible(bool shouldBeVisible)
    {
        for (int i = 0; i < nCmmds; i++)
        {
            GameObject cell = matrixObject.transform.GetChild(i).gameObject;
            cell.SetActive(shouldBeVisible);
        }
    }

    /* ------------------------------------------- COMMUNICATION ------------------------------------------- */

    // This function is called by the MedusaTCPClient whenever a packet is received in order to interpret it
    public void interpretMessage(string message)
    {
        Debug.Log("Received from server: " + message);
        string eventType = messageInterpreter.decodeEventType(message);
        switch (eventType)
        {
            case "play":
                if (state != STATE_WAITING_PARAMS)
                {
                    state = RUN_STATE_RUNNING;
                    mustStartTrial = true;
                }
                break;
            case "pause":
                if (state != STATE_WAITING_PARAMS)
                    state = RUN_STATE_PAUSED;
                break;
            case "resume":
                if (state != STATE_WAITING_PARAMS)
                    state = RUN_STATE_RUNNING;
                break;
            case "stop":
                if (state != STATE_WAITING_PARAMS)
                {
                    state = RUN_STATE_STOP;
                    innerstate = STATE_RUNNING_PREVTEXT;
                    mustClose = true;
                }
                break;
            case "restart":
                if (state != STATE_WAITING_PARAMS)
                {
                    state = RUN_STATE_READY;
                    innerstate = STATE_RUNNING_PREVTEXT;
                }
                break;
            case "setParameters":
                // The main thread will detect that parameters are here using Update() and will call onParametersReady() itself
                parameters = messageInterpreter.decodeParameters(message);
                Debug.Log("Parameters received.");
                break;
            case "selection":
                // MEDUSA has selected a new command!
                string selection_uid = messageInterpreter.decodeSelection(message);
                onSelectedCommand(selection_uid);
                break;
            case "exception":
                string exception = messageInterpreter.decodeException(message);
                Debug.LogError("Exception from client, aborting: " + exception);
                state = RUN_STATE_STOP;
                innerstate = STATE_RUNNING_PREVTEXT;

                tcpClient.socketConnection.Close();
                tcpClient.socketConnection = null;
                mustClose = true;
                break;
            default:
                Debug.LogError("Unknown action!");
                break;
        }
    }

    // This function is called by the main thread when parameters are ready
    void onParametersReady()
    {
        // Extract the parameters
        fpsResolution = parameters.fps_resolution;

        // Set up the fixedDeltaTime to the desired frame rate for the clock
        Application.targetFrameRate = -1;
        Time.fixedDeltaTime = 1 / ((float)fpsResolution);

        // MATRIX
        matrix = parameters.matrix;
        matrixItemSequence = new List<int[]>();
        matrixCurrentTimeShift = 0;
        matrixSequenceLength = matrix.item_list[0].sequence.GetLength(0);

        for (int i = 0; i < nCmmds; i++)
        {
            GameObject cell = matrixObject.transform.GetChild(i).gameObject;

            MessageInterpreter.ParameterDecoder.Target item = matrix.item_list[i];

            matrixItemSequence.Add(item.sequence);
        }

        setMatrixVisible(true);


        // Change state
        state = RUN_STATE_READY;
        ServerMessage sm = new ServerMessage("ready");
        tcpClient.SendMessage(sm.ToJson());
        setInformationText("Waiting for start...");
    }

    // This function is called when a command is selected from MEDUSA
    void onSelectedCommand(string selectionUid)
    {
        // Store the new result
        lastResultUid = selectionUid;
        state = STATE_SELECTION_RECEIVED;
        mustShowResult = true;

        if (Enum.TryParse<Direction>(selectionUid, out Direction dir))
            gameManager.Move(dir);
    }

    // This function returns the current timestamp in seconds from the Unix epoch (1/1/1970)
    double getCurrentTimeStamp()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long unixTimeMilliseconds = now.ToUnixTimeMilliseconds();
        double unixTimeSeconds = Convert.ToDouble(unixTimeMilliseconds) / 1000.0;
        return unixTimeSeconds;
    }

    /* ----------------------------------------- ONLINE LOOPS ----------------------------------------- */

    // Loop for "Online" mode: selecting items from the matrix
    void loopTest()
    {
        // First: show starting text
        if (innerstate == STATE_RUNNING_PREVTEXT)
        {
            setInformationText("Starting...");
        }
        // Second: standby 
        else if (innerstate == STATE_RUNNING_IDDLE)
        {
            setInformationText("");
        }
        // NOTE: the two first training steps are ignored as they are focused to highlight the target
        // Test loop, third: flickering
        else if (innerstate == STATE_RUNNING_FLICKERING)
        {
            // First: check if the reference is starting a new trial
            if (matrixCurrentTimeShift == 0)
            {
                // It is starting a new trial, so the timestamp must be recorded and sent
                if (cycleTestCounter < testCycles)
                {
                    double currentTime = getCurrentTimeStamp();
                    ServerMessage sm = new ServerMessage("test");
                    sm.addValue("onset", currentTime);
                    sm.addValue("trial", currentTestTarget);
                    sm.addValue("matrix_idx", 0);
                    sm.addValue("level_idx", 0);
                    sm.addValue("unit_idx", 0);
                    tcpClient.SendMessage(sm.ToJson());
                }
                // Important note: the previous IF statement prevents the system to send the onset when cycleTestCounter==testCycles, 
                // In such a way, the next stage lets the last cycle to be displayed completely. Otherwise, the last cycle onset 
                // would be sent to MEDUSA and immediately the flickering would stop, sending the "processPlease" command
                cycleTestCounter++;
            }
            // Check how many cycles have been displayed
            if (cycleTestCounter > testCycles)
            {
                cycleTestCounter = 0;
                currentTestTarget++;
                resetMatrix();

                // Request MEDUSA to process the trial
                state = STATE_WAITING_SELECTION;
                ServerMessage sm = new ServerMessage("processPlease");
                tcpClient.SendMessage(sm.ToJson());
            }
            else
            {
                // Make the flashings
                for (int i = 0; i < nCmmds; i++)
                {
                    int value = matrixItemSequence[i][matrixCurrentTimeShift];
                    GameObject cell = matrixObject.transform.GetChild(i).gameObject;

                    if (value == 0)
                    {
                        changeItemColor(cell, Color.black);
                    }
                    else
                    {
                        changeItemColor(cell, Color.white);
                    }
                }

                // Update the current index
                matrixCurrentTimeShift++;
                if (matrixCurrentTimeShift >= matrixSequenceLength)
                {
                    matrixCurrentTimeShift = 0;
                }
            }
        }
    }

    /* ------------------------------------------- CO-ROUTINES ------------------------------------------- */

    // This thread controls the FPS rate
    IEnumerator monitorFPS()
    {
        while (true)
        {
            yield return new WaitForSeconds(1);
            updateUpdateCountPerSecond = updateCount;
            updateFixedUpdateCountPerSecond = fixedUpdateCount;

            updateCount = 0;
            fixedUpdateCount = 0;
        }
    }

    // This thread controls the timings of a running cycle. When the flashings start, this routine ends.
    IEnumerator innerRunningCycle()
    {
        if (innerstate <= STATE_RUNNING_PREVTEXT)
        {
            Debug.Log("Running: starting...");
            innerstate = STATE_RUNNING_PREVTEXT;
            yield return new WaitForSeconds((float)tPrevText);
        }

        if (innerstate <= STATE_RUNNING_IDDLE)
        {
            Debug.Log("Running: iddle.");
            innerstate = STATE_RUNNING_IDDLE;
            yield return new WaitForSeconds((float)tPrevIddle);
        }

        if (innerstate <= STATE_RUNNING_FLICKERING)
        {
            Debug.Log("Running: flickering.");
            innerstate = STATE_RUNNING_FLICKERING;
        }
    }

    // This thread controls the timings of the finished run
    IEnumerator finishingCycle()
    {
        Debug.Log("Finishing...");
        finishingstate = STATE_FINISHING_IDDLE;
        yield return new WaitForSeconds((float)tPrevIddle);

        finishingstate = STATE_FINISHING_TEXT;
        yield return new WaitForSeconds((float)tFinishText);

        // Send the confirmation that the Unity's client has finished the execution
        // NOTE: we have waited the test to show to assure that enough samples after the last onset have been recorded in the MANAGER of MEDUSA
        ServerMessage sm = new ServerMessage("finish");
        tcpClient.SendMessage(sm.ToJson());
    }

    // This thread controls the timings for closing the application.
    IEnumerator closingApplication()
    {
        Debug.Log("Closing...");
        closingstate = STATE_CLOSING_TEXT;
        yield return new WaitForSeconds((float)tFinishText);

        closingstate = STATE_CLOSING_FINAL;
    }

    IEnumerator showingResult()
    {
        Debug.Log("Showing the result...");
        resultstate = STATE_RESULT_SHOW;
        yield return new WaitForSeconds((float)tPrevText);

        resultstate = STATE_RESULT_IDDLE;
        yield return new WaitForSeconds((float)tPrevIddle);

        resultstate = STATE_RESULT_END;
        Debug.Log("Showing result finished...");
    }

/* ------------------------------------------- RASTER LATENCIES UTILS ------------------------------------------- */
    public struct Rect
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
    }

}