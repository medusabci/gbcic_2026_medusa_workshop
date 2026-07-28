// MEDUSA-PLATFORM 
// v2022.0 CHAOS
// www.medusabci.com

// MessageInterpreter for the c-VEP Speller (Unity app)
//      > Author: Víctor Martínez-Cagigal
//      > Date: 19/05/2022

// Versions:
//      - v1.0 (19/05/2022):    Initial message interpreter


using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using Newtonsoft.Json;
using System.IO;

public class MessageInterpreter
{
    // This class provides a framework to decode all types of messages that are sent by MEDUSA-PLATFORM
    public MessageInterpreter()
    {
    }

    /* ----------------------------------- DECODING FUNCTIONS ------------------------------------ */
    public string decodeEventType(string message)
    {
        return EventTypeDecoder.getEventTypeFromJSON(message);
    }

    public ParameterDecoder decodeParameters(string message)
    {
        return ParameterDecoder.getParametersFromJSON(message);
    }

    public string decodeException(string message)
    {
        return ExceptionDecoder.getExceptionFromJSON(message);
    }

    public string decodeSelection(string message)
    {
        return SelectionDecoder.getSelectionFromJSON(message);
    }

    /* ----------------------------------- DECODING CLASSES ------------------------------------ */
    /** Class to decode the event_type first. */
    public class EventTypeDecoder
    {
        public string event_type;

        public static string getEventTypeFromJSON(string jsonString)
        {
            EventTypeDecoder event_type = JsonUtility.FromJson<EventTypeDecoder>(jsonString);
            return event_type.event_type;
        }
    }

    /** Class to decode parameters from the c-VEP Speller app (v1.0)
     * Check out the associated app_controller.py.
     **/
    public class ParameterDecoder
    {
        // Matrix
        public Matrix matrix; 

        // RunSettings
        public float fps_resolution;

        public static ParameterDecoder getParametersFromJSON(string jsonString)
        {
            ParameterDecoder p = JsonConvert.DeserializeObject<ParameterDecoder>(jsonString);
            return p;
        }

        public class Matrix
        {
            public List<Target> item_list { get; set; }
        }

        public class Target
        {
            public string uid { get; set; }
            public int[] sequence { get; set; }
        }
    }

    public class ExceptionDecoder
    {
        public string exception;

        public static string getExceptionFromJSON(string jsonString)
        {
            ExceptionDecoder exception = JsonUtility.FromJson<ExceptionDecoder>(jsonString);
            return exception.exception;
        }
    }

    public class SelectionDecoder
    {
        public string selection_uid;

        public static string getSelectionFromJSON(string jsonString)
        {
            SelectionDecoder s = JsonUtility.FromJson<SelectionDecoder>(jsonString);
            return s.selection_uid;
        }
    }
    
}

public class ServerMessage
{
    public Dictionary<string, object> message = new Dictionary<string, object>();

    public ServerMessage(string action)
    {
        message.Add("event_type", action);
    }

    public void addValue(string key, object value)
    {
        message.Add(key, value);
    }

    public string ToJson()
    {
        return JsonConvert.SerializeObject(message);
    }
}