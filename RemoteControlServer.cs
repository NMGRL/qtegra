// RegisterAssembly: DefinitionsCore.dll
// RegisterAssembly: BasicHardware.dll
// RegisterAssembly: PluginManager.dll
// RegisterAssembly: HardwareClient.dll
// RegisterSystemAssembly: System.dll
// RegisterAssembly: SpectrumLibrary.dll
// RegisterSystemAssembly: System.Xml.dll
// RegisterAssembly: Util.dll

/*
Copyright 2011 Jake Ross
 Licensed under the Apache License, Version 2.0 (the "License"); you may not use
 this file except in compliance with the License. You may obtain a copy of the
 License at
    http://www.apache.org/licenses/LICENSE-2.0
Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.

__version__=19.10
*/


using System.IO;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Thermo.Imhotep.BasicHardware;
using Thermo.Imhotep.Definitions.Core;
using Thermo.Imhotep.SpectrumLibrary;
using Thermo.Imhotep.Util;
static class Config
{
    public static List<string> ARGUS_DETECTOR_NAMES = new List<string>(new string[]{
                                                                  "CUP 4,H2",
                                                                  "CUP 3,H1",
                                                                  "CUP 2,AX",
                                                                  "CUP 1,L1",
                                                                  "CUP 0,L2",
                                                                  "CDD 0,CDD"});

    public static List<string> HELIX_MC_DETECTOR_NAMES = new List<string>(new string[]{
                                                                 "CUP 4,H2",
                                                                 "CUP 3,H1",
																 "CUP 2,AX",
																 "CUP 1,L1",
																 "CUP 0,L2",
																 "CDD 0,L2(CDD)",
																 "CDD 1,L1(CDD)",
																 "CDD 2,AX(CDD)",
																 "CDD 3,H1(CDD)",
																 "CDD 4,H2(CDD)"});

	public static List<string< HELIX_SFT_DETECTOR_NAMES = new List<string>(new string[]{
	                                                            "CUP 1,H1",
	                                                            "CUP 0,AX",
	                                                            "CDD 0,CDD"});



    // user configurable attributes
    public static int port = 1069;
    public static bool use_udp= true;
    public static bool tag_data= true;
    public static bool use_beam_blank= true;
    public static double magnet_move_threshold=0.25;// threshold to trigger stepped magnet move in DAC units
    public static int magnet_steps=20; // number of steps to divide the total magnet displacement
    public static int magnet_step_time=100; //ms to delay between magnet steps

}


class RemoteControl
{

    private static Socket UDP_SOCK;
    private static Thread SERVER_THREAD;
    private static TcpListener TCPLISTENER;

    private static bool MAGNET_MOVING=false;

    private static double LAST_Y_SYMMETRY=0;
    private static bool IsBLANKED=false;
    private static Dictionary<string, double> m_defl = new Dictionary<string, double>();

    private const int OPEN = 1;
    private const int CLOSE = 0;

    public static IRMSBaseCore Instrument;
    public static IRMSBaseMeasurementInfo m_restoreMeasurementInfo;

    private static string SCAN_DATA;
    private static object m_lock=new object();


	private static List<string> DETECTOR_NAMES;


    public static void Main ()
    {

        // Configure the proper instrument
        // ================================================================================
        //Instrument= ArgusMC;
        //DETECTOR_NAMES = Config.ARGUS_DETECTOR_NAMES

        //Instrument= HelixMC;
        //DETECTOR_NAMES = Config.HELIX_MC_DETECTOR_NAMES
        // ================================================================================

        //Instrument= HelixSFT
        DETECTOR_NAMES = Config.HELIX_SFT_DETECTOR_NAMES

        //init parameters
        Instrument.GetParameter("Y-Symmetry Set", out LAST_Y_SYMMETRY);

        GetTuningSettings();
        PrepareEnvironment();

        //mReportGains();

        //list attributes
        //listattributes(Instrument.GetType(), "instrument");

        //setup data recording
        InitializeDataRecord();

        if (Config.use_udp)
        {
            UDPServeForever();
        }
        else
        {
            TCPServeForever();
        }

    }

    //====================================================================================================================================
    //
    //  Commands are case sensitive and in CamelCase
    //  do not include the "<" or ">" in the commands.
    //  e.g SetTrapVoltage 120 not SetTrapVoltage <120>
    //  Commands:
    //      GetTuningSettingsList #return a comma separated string
    //      SetTuningSettings
    //      GetData returns tagged data e.g. H2,aaa,L1,bbb,CDD,ccc
    //      SetIntegrationTime <seconds>
    //      GetIntegrationTime <seconds>
    //      BlankBeam <true or false> if true set y-symmetry to -50 else return to previous value

    //===========Cup/SubCup Configurations==============================
    //      GetCupConfigurationList
    //      GetSubCupConfigurationList
    //      GetActiveCupConfiguration
    //      GetActiveSubCupConfiguration
    //      GetSubCupParameters returns list of Deflection voltages and the Ion Counter supply voltage
    //      SetSubCupConfiguration <sub cup name>
    //      ActivateCupConfiguration <cup name> <sub cup name>

    //===========Ion Counter============================================
    //      ActivateIonCounter <detname>
    //      DeactivateIonCounter <detname>

    //===========Ion Pump Valve=========================================
    //      Open  #open the Ion pump to the mass spec
    //      Close #closes the Ion pump to the mass spec
    //      GetValveState #returns True for open false for close

    //===========Magnet=================================================
    //      GetMagnetDAC
    //      SetMagnetDAC <value> #0-10V
    //      GetMagnetMoving
    //      @SetMass <value>,<string cupName> #deactivates the CDDs if they are active

    //==========Detectors===============================================
    //      ProtectDetector <name>,<On/Off>
	//      GetDeflection <name>
	//      SetDeflection <name>,<value>
	//      GetIonCounterVoltage
	//      SetIonCounterVoltage <value>
	//      GetGain <name>
	//      GetDeflections <name>[,<name>,...]

	//==================================================================
	//		Error Responses:
	//			Error: Invalid Command   - the command is poorly formated or does not exist.
	//			Error: could not set <hardware> to <value>

     //==========Generic Device===============================================
	//      GetParameter <name> -  name is any valid device currently listed in the hardware database
	//      SetParameter <name>,<value> -  name is any valid device currently listed in the hardware database
	//      GetParameters <name>[,<name>,...]
	//====================================================================================================================================

	private static string ParseAndExecuteCommand (string cmd)
	{

		string result = "Error: Invalid Command";
		Logger.Log(LogLevel.UserInfo, String.Format("Executing {0}", cmd));

		string[] args = cmd.Trim().Split (' ');
		string[] pargs;
		string jargs;

        double r;
        switch (args[0]) {

        case "GetDeflections":
            result = GetDeflections(args);
            break;
        case "GetParameters":
            result = GetParameters(args);
            break;

		case "GetTuningSettingsList":
			result = GetTuningSettings();
			break;

		case "SetTuningSettings":
			if(SetTuningSettings(args[1]))
			{
				result="OK";
			}
			else
			{
				result=String.Format("Error: could not set tuning settings {0}",args[1]);
			}
			break;

		case "GetData":
			result=SCAN_DATA;
			break;

		case "SetIntegrationTime":
			result=SetIntegrationTime(Convert.ToDouble(args[1]));
			break;
		case "GetIntegrationTime":
			result=GetIntegrationTime();
			break;

		case "BlankBeam":

			if (!Config.use_beam_blank)
			{
				result="OK";
				break;
			}

			double yval=LAST_Y_SYMMETRY;
			bool blankbeam=false;
			if (args[1].ToLower()=="true")
			{
				if(!IsBLANKED)
				{
					//remember the non blanking Y-Symmetry value
					Instrument.GetParameter("Y-Symmetry Set", out LAST_Y_SYMMETRY);
					yval=-50;
					IsBLANKED=true;
					blankbeam=true;
				}
			}
			else
			{
				if(IsBLANKED)
				{
					IsBLANKED=false;
					blankbeam=true;
					result=SetParameter("Y-Symmetry Set",yval);
				}
			}

			result="OK";
			if(blankbeam)
			{
				result=SetParameter("Y-Symmetry Set",yval);
			}

			break;

//============================================================================================
//   Cup / SubCup Configurations
//============================================================================================
        case "GetCupConfigurationList":
            List<string> cup_names = GetCupConfigurations ();
            result = string.Join ("\r", cup_names.ToArray ());
            break;

        case "GetSubCupConfigurationList":
            string config_name = args[1];
            List<string> sub_names = GetSubCupConfigurations (config_name);
            result = string.Join ("\r", sub_names.ToArray ());
            break;

        case "GetActiveCupConfiguration":
            result=Instrument.CupConfigurationDataList.GetActiveCupConfiguration().Name;
            break;

        case "GetActiveSubCupConfiguration":
            result=Instrument.CupConfigurationDataList.GetActiveSubCupConfiguration().Name;
            break;

        case "GetSubCupParameters":
            result=GetSubCupParameters();
            break;

        case "ActivateCupConfiguration":
            result = mActivateCup(args[1], args[2]);
            break;
//============================================================================================
//   Ion Counter
//============================================================================================
		case "ActivateIonCounter":
			result = SetIonCounterState(args[1], true);
			break;
		case "DeactivateIonCounter":
			result = SetIonCounterState(args[1], false);
			break;

//============================================================================================
//   Ion Pump Valve
//============================================================================================
        case "Open":
        	Logger.Log(LogLevel.Debug, String.Format("Executing {0}", cmd));
			jargs=String.Join(" ", Slice(args,1,0));
			result=SetParameter(jargs,OPEN);
			break;

		case "Close":
			jargs=String.Join(" ", Slice(args,1,0));
			Logger.Log(LogLevel.Debug, String.Format("Executing {0}", cmd));
			result=SetParameter(jargs,CLOSE);
			break;

		case "GetValveState":
			Logger.Log(LogLevel.Debug, String.Format("Executing {0}", cmd));

			jargs=String.Join(" ", Slice(args,1,0));
			result=GetValveState(jargs);
			Logger.Log(LogLevel.Debug, String.Format("Valve state {0}", result));
			break;

//============================================================================================
//   Magnet
//============================================================================================
        case "GetMagnetDAC":
            if (Instrument.GetParameter("Field Set", out r))
            {result=r.ToString();}
            break;

        case "SetMagnetDAC":
            result=SetMagnetDAC(Convert.ToDouble(args[1]));
            break;
        case "GetMagnetMoving":
            result=GetMagnetMoving();
            break;
        case "SetMass":
            //Adapted from CSIRORemoteControlServer.cs
			jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(',');
		    result = SetMass(Convert.ToDouble(pargs[0]),(pargs.Length>1) ? pargs[1] : null);
            break;
//============================================================================================
//    Source Parameters
//============================================================================================
		case "GetHighVoltage":
			if(Instrument.GetParameter("Acceleration Reference Set",out r))
			{
				result=(r*1000).ToString();
			}
			break;

		case "SetHighVoltage":
			result=SetParameter("Acceleration Reference Set", Convert.ToDouble(args[1])/1000.0);
			break;
		case "GetHV":
			if(Instrument.GetParameter("Acceleration Reference Set",out r))
			{
				result=(r).ToString();
			}
			break;

		case "SetHV":
			result=SetParameter("Acceleration Reference Set", Convert.ToDouble(args[1])/1000.0);
			break;
//============================================================================================
//    Detectors
//============================================================================================
        case "ProtectDetector":
            pargs=args[1].Split(',');
            ProtectDetector(pargs[0], pargs[1]);
            result="OK";
            break;

        case "GetDeflection":
            jargs=String.Join(" ", Slice(args,1,0));
            if(Instrument.GetParameter(String.Format("Deflection {0} Set",jargs), out r))
            {result=r.ToString();}
            break;

        case "SetDeflection":
            jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(',');
            result=SetParameter(String.Format("Deflection {0} Set",pargs[0]),Convert.ToDouble(pargs[1]));
            break;

        case "GetIonCounterVoltage":
            if(Instrument.GetParameter("CDD Supply Set",out r))
            {result=r.ToString();}
            break;

        case "SetIonCounterVoltage":
            result=SetParameter("CDD Supply Set", Convert.ToDouble(args[1]));
            break;

        case "GetGain":
            jargs=String.Join(" ", Slice(args,1,0));
            result=GetGain(jargs);
            break;

        case "SetGain":
            jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(',');
            result="OK";
            SetGain(pargs[0], Convert.ToDouble(pargs[1]));
            break;

//============================================================================================
//    Generic
//============================================================================================
		case "GetParameter":
		    jargs=String.Join(" ", Slice(args,1,0));
			if(Instrument.GetParameter(jargs, out r))
			{
				result=r.ToString();
			}
			break;

		case "SetParameter":
		    jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(',');
		   result=SetParameter(pargs[0], Convert.ToDouble(pargs[1]));
		   break;
		}
		log(String.Format("{0} => {1}", cmd, result));
		return result;
	}
//============================================================================================
//    EOCommands
//============================================================================================

	private static bool PrepareEnvironment()
    {
        m_restoreMeasurementInfo=Instrument.MeasurementInfo;
        return Instrument.ScanTransitionController.InitializeScriptScan(m_restoreMeasurementInfo);
    }

    public static void InitializeDataRecord()
    {
        // attach a handler to the ScanDataAvailable Event
        Instrument.ScanDataAvailable+=ScanDataAvailable;
    }

    public static void Dispose()
    {
        Logger.Log (LogLevel.UserInfo, "Stop Server");

        // deattach the handler from the ScanDataAvailable Event
        Instrument.ScanDataAvailable-=ScanDataAvailable;

        //shutdown the server
        if (Config.use_udp)
        {
            UDP_SOCK.Close();
        }
        else
        {
            TCPLISTENER.Stop();
        }

        SERVER_THREAD.Abort();

    }

//====================================================================================================================================
//Qtegra Methods
//====================================================================================================================================
    public static string GetDeflections(string[] args)
    {
        List<string> data = new List<string>();
        double v;

        string dets = String.Join(" ", Slice(args,1,0));

        foreach(string k in dets.Split(','))
        {
            if(!Instrument.GetParameter(String.Format("Deflection {0} Set", k), out v))
            {
                v = 0;
            }
            data.Add(v.ToString());
        }

        return string.Join(",",data.ToArray());
    }
    public state string GetParameters(string[] args)
    {
        List<string> data = new List<string>();
        double v;
        string param;
        foreach(string k in args[1].Split(','))
        {
           if (!Instrument.GetParameter(param, out v))
           {
                v = 0;
           }
           data.Add(v.ToString());
        }

        return string.Join(",",data.ToArray());

    }
	public static string mActivateCup(string a, string b)
    {
        string result;
        if(ActivateCupConfiguration(a, b))
        {
            result="OK";
        }
        else
        {
            result=String.Format("Error: could not set cup={0}, sub cup to {1} ", a, b);
        }
        return result;
    }
    public static string GetMagnetMoving()
    {
        if (MAGNET_MOVING)
        {
            return "True";
        }
        else
        {
            return "False";
        }

    }
    public static void ProtectDetector(string detname, string state)
    {
        string param=String.Format("Deflection {0} Set",detname);
        if (state.ToLower()=="on")
        {
            double v;
            Instrument.GetParameter(param, out v);
            m_defl[detname]=v;
            SetParameter(param, 2000);
        }
        else
        {
            if (m_defl.ContainsKey(detname))
            {
                SetParameter(param, m_defl[detname]);
            }

	    }
	}
	public static String SetIonCounterState(String name, bool state)
	{
		if (state)
		{
			Logger.Log (LogLevel.UserInfo, "Setting IonCounterState True");
		}
		else
		{
			Logger.Log (LogLevel.UserInfo, "Setting IonCounterState False");
		}

        // Adapted from CSIRORemoteControlServer.cs
        // activate the CDDs...
		if (!ActivateCounterCup(true, name))
		{
		    return String.Format("Could not activate {0}.", name};
		}
		Thread.Sleep(1000); // Wait for a little time for the activation to take effect...

		// Restart the monitor scan, to ensure data is reported for the CDD...
		if (!RunMonitorScan(get_scan_mass()))
		{
			return "Could not restart monitor scan.";
		}

	    return "OK";
	}

	public static string GetValveState(string hwname)
	{
		string result="Error";
		double rawValue;
		if(Instrument.GetParameter(hwname,out rawValue))
		{
			if (rawValue==OPEN)
			{
				result="True";
			}
			else
			{
				result="False";
			}
		}
		return result;

	}

	public static string SetParameter(string hwname, int val)
	{
		string result="OK";
		if (!Instrument.SetParameter(hwname, val))
		{
			result=String.Format("Error: could not set {0} to {1}", hwname, val);
		}
		return result;
	}

	public static string SetParameter(string hwname, double val)
	{	string result="OK";

		if (!Instrument.SetParameter(hwname, val))
		{
			result=String.Format("Error: could not set {0} to {1}", hwname, val);
		}
		return result;
	}

    private static string SetMass(double m, string cup)
	{
		// First deactivate CDDs to protect them.
		if (!(DeactivateAllCDDs()=="OK"))
		{
			return "Error: Could not deactivate CDDs. Mass not changed.";
		}

		// If mass is not for AX cup, convert the mass to a value for the AX cup
		double AXMass = m;
		log(AXMass.ToString());
		if (cup!="AX")
		{
			double? tempMass = ConvertMassToAxial(cup,m);
			if (!tempMass.HasValue)
			{
				return "Error: Cup not recognized";
			}
			else
			{
				AXMass = tempMass ?? 0;
			}
		}

		// Now change the mass.
		double initialMass = Instrument.MeasurementInfo.ActualScanMass;
		double currentMass = initialMass;
		int count = 0;
		while (currentMass != AXMass) //For large changes, do it stepwise to avoid ringing
		{
			if ((Math.Abs(currentMass-AXMass)/currentMass) < MAGNET_MOVE_THRESHOLD_REL_MASS) //for smallk changes
			{
				currentMass = AXMass;
			}
			else // for large changes only change by a certain amount and repeat
			{
				currentMass += currentMass*MAGNET_MOVE_THRESHOLD_REL_MASS*((AXMass>currentMass) ? 1 : -1);
			}
			if (!RunMonitorScan(currentMass))
			{
				return "Error: Could not change mass.";
			}
			if (MAGNET_STEP_TIME>0)
            {
                Thread.Sleep(MAGNET_STEP_TIME);
            }
            count++;
		}
        Logger.Log(LogLevel.UserInfo,String.Format("Mass was changed from {0} to {1} on the AX in {2} step(s).",initialMass,currentMass,count));
		return "OK";
	}

	public static string SetMagnetDAC(double d)
	{

		string result="OK";
		double current_dac;

		if (Instrument.GetParameter("Field Set", out current_dac))
		{
			double dev=Math.Abs(d-current_dac);
			if (dev>Config.magnet_move_threshold)
			{
                Thread t= new Thread(delegate(){mSetMagnetDAC(d,dev,current_dac);});
                t.Start();

                result="OK";
            }
            else
            {
                result=SetParameter("Field Set", d);
            }
        }
        return result;

    }

    public static void mSetMagnetDAC(double d, double dev, double current_dac)
    {
        MAGNET_MOVING=true;
        //incrementally move the magnet to eliminate "ringing"
        double step=dev/Config.magnet_steps;
        int sign=1;

        if (current_dac>d)
        {
            sign=-1;
        }

        for(int i=1; i<=Config.magnet_steps; i++)
        {
            SetParameter("Field Set", current_dac+sign*i*step);
            if (Config.magnet_step_time>0)
            {
                Thread.CurrentThread.Join(Config.magnet_step_time);
            }
        }
        MAGNET_MOVING=false;
    }

    public static string GetIntegrationTime()
    {
        string result=String.Format("{0}", Instrument.MeasurementInfo.IntegrationTime*0.001);
        return result;
    }

    public static string SetIntegrationTime(double t)
    {
        //t in ms
        string result="OK";
        IRMSBaseMeasurementInfo nMI= new IRMSBaseMeasurementInfo(Instrument.MeasurementInfo);
        nMI.IntegrationTime = t*1000;
        if (!Instrument.ScanTransitionController.StartMonitoring (nMI))
        {
            Logger.Log(LogLevel.UserError, "Could not start the modified monitor");
            result=String.Format("Error: could not set integration time to {0}",t);
        }

        return result;
    }

    public static void SetGain(String name, double v)
    {
        string id = mGetDetectorIdentifier(name);
        foreach (UFCCalibrationData item in Instrument.UFCCalibrationData)
        {
            if (item.Identifier==id)
            {
                item.Gain=v;
                break;
            }
        }

    }

    public static string GetGain(String name)
    {
        double gain=0;
        string id = mGetDetectorIdentifier(name);
        foreach (UFCCalibrationData item in Instrument.UFCCalibrationData)
        {
            if (item.Identifier==id)
            {
                gain=Convert.ToDouble(item.Gain);
                break;
            }
        }

	   return String.Format("{0}",gain);
	}

    private static string get_cup_name(IRMSBaseCollectorItem item)
    {
        foreach (string detname in DETECTOR_NAMES)
        {
            string[] args=detname.Split(',');
            if(args[0]==item.Identifier)
            {
                return args[1];
            }
        }
        return "foo";
    }

	public static void ScanDataAvailable(object sender, EventArgs<Spectrum> e)
	{
		lock(m_lock)
		{

			List<string> data = new List<string>();
			Spectrum spec = e.Value.Clone() as Spectrum;
			IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();

			foreach (Series series in spec)
			{
				foreach (SpectrumData point in series)
				{
					//get the name of the detector
					foreach (IRMSBaseCollectorItem item in cupData.CollectorItemList)
					{
						if (item.Mass==point.Mass)
						{	string cupName=get_cup_name(item);


							data.Add(point.Analog.ToString());
							if (Config.tag_data)
							{
								data.Add(cupName);
							}

							break;
						}
					}

				}
			}
			data.Reverse();
			SCAN_DATA=string.Join(",",data.ToArray());
		}
	}

//	private static bool UpdateMonitorScan(bool enable)
//	{
//        IRMSBaseCupConfigurationData config = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
//        IRMSBaseMeasurementInfo monitor_measurement_info = new IRMSBaseMeasurementInfo(
//            Instrument.MeasurementInfo.ScanType,
//            Instrument.MeasurementInfo.IntegrationTime,
//            Instrument.MeasurementInfo.SettlingTime,
//            config.CollectorItemList.GetMasterCollectorItem().Mass.Value,
//            config.CollectorItemList,
//            config.MassCalibration
//        );
//        success = Instrument.ScanTransitionController.StartMonitoring(monitor_measurement_info);
//        success = Instrument.ScanTransitionController.StartMonitoring(monitor_measurement_info);
//        if (!success) Logger.Log(LogLevel.UserError, "Failed to update monitoring.");
//        else if (enable) Logger.Log(LogLevel.UserInfo, "Monitoring has been updated.");
//        return success;
//	}

	private static bool RunMonitorScan (double? mass)
	{
		IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
		IRMSBaseMeasurementInfo newMeasurementInfo =
			new IRMSBaseMeasurementInfo(
				Instrument.MeasurementInfo.ScanType,
				Instrument.MeasurementInfo.IntegrationTime,
				Instrument.MeasurementInfo.SettlingTime,
				(mass.HasValue) ? mass.Value : cupData.CollectorItemList.GetMasterCollectorItem().Mass.Value,
				cupData.CollectorItemList,
				cupData.MassCalibration
				);
		Instrument.ScanTransitionController.StartMonitoring(newMeasurementInfo);
		return true;
	}

	private static string GetTuningSettings()
	{
		TuneSettingsManager tsm = new TuneSettingsManager(Instrument.Id);
		List<string> result = new List<string>();
		tsm.EntryType= typeof(TuneSettings);
		result.AddRange(tsm.GetEntries());
		return string.Join(",",result.ToArray());
	}

	private static bool SetTuningSettings(string name)
	{
		TuneSettingsManager tsm = new TuneSettingsManager(Instrument.Id);
		TuneSettings tuneSettings = tuneSettings = tsm.ReadEntry(name) as TuneSettings; ;
		//TuneSettings tuneSettings =tsm.ReadEntry(name);
		TuneParameterBlock tuneBlock = null;
		if (tuneSettings == null)
		{
			Logger.Log(LogLevel.UserError, String.Format("Could not load tune setting \'{0}\'.", name));
			return false;
		}
		else
		{
			tuneBlock = tuneSettings.Object as TuneParameterBlock;
			if (tuneBlock == null)
			{
				Logger.Log(LogLevel.UserError, string.Format("Tune setting \'{0}\' could not convert to tuneblock.", name));
				return false;
			}
			else
			{
				Instrument.TuneParameters.Parameters = tuneBlock;
				Logger.Log(LogLevel.UserInfo, string.Format("Tune setting : \'{0}\' successfully load!", name));
			}
		}
		return true;
	}

	private static string GetSubCupParameters()
	{
	   // change parameters to values relevant for your system
	    // default is Argus VI c. 2010
		List<string> parameters = new List<string>(new string[]{
												"Deflection H2 Set",
												"Deflection H1 Set",
												"Deflection AX Set",
												"Deflection L1 Set",
												"Deflection L2 Set",
												"Deflection CDD Set",
												"CDD Supply Set",
													});
		double value=0.0;
		List<string> data = new List<string>();
		foreach (var item in parameters)
		{
			if(Instrument.GetParameter(item, out value))
			{
				data.Add(String.Format("{0},{1}", item, value));
			}
		}
		return String.Join(";", data.ToArray());
	}
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	// <summary>
	// Get all cup configurations names from the system.
	// </summary>
	// <returns>A list of cup configuration names.</returns>
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static List<string> GetCupConfigurations ()
	{
		List<string> result = new List<string>();
		foreach (var item in Instrument.CupConfigurationDataList) {
			result.Add (item.Name);
		}
		return result;
	}
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	// <summary>
	// Get all sub cup configurations names from a cup cupconfiguration.
	// </summary>
	// <param name="cupConfigurationName"></param>
	// <returns>A list of sub cup configuration names.</returns>
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static List<string> GetSubCupConfigurations (string cupConfigurationName)
	{
		IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.FindCupConfigurationByName (cupConfigurationName);
		if (cupData == null) {
			Logger.Log (LogLevel.UserError, String.Format ("Could not find cup configuration \'{0}\'.", cupConfigurationName));
			return null;
		}
		
		List<string> result = new List<string>();
		foreach (var item in cupData.SubCupConfigurationList) {
			result.Add (item.Name);
		}
		

		return result;
	}
	
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	// <summary>
	// Activate a cup configuration and sub cup configuration.
	// </summary>
	// <param name="cupConfigurationName">The cup configuration name.</param>
	// <param name="subCupConfigurationName">The sub cup configuration name.</param>
	// <param name="mass">A nullable mass value. If has a value then use the mass for the monitor scan; otherwise use the master cup mass for the monitor scan.</param>
	// <returns><c>True</c> if successfull; otherwise <c>false</c>.</returns>
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static bool ActivateCupConfiguration (string cupConfigurationName, string subCupConfigurationName)
	{
		
		//Console.WriteLine (cupConfigurationName);
		//Console.WriteLine (subCupConfigurationName);
		IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.FindCupConfigurationByName (cupConfigurationName);
		if (cupData == null) {
			Logger.Log (LogLevel.UserError, String.Format ("Could not find cup configuration \'{0}\'.", cupConfigurationName));
			return false;
		}
		IRMSBaseSubCupConfigurationData subCupData = cupData.SubCupConfigurationList.FindSubCupConfigurationByName (subCupConfigurationName);
		if (subCupData == null) {
			Logger.Log (LogLevel.UserError, String.Format ("Could not find sub cup configuration \'{0}\' in cup configuration.", subCupConfigurationName, cupConfigurationName));
			return false;
		}
		Instrument.CupConfigurationDataList.SetActiveItem (cupData.Identifier, subCupData.Identifier, Instrument.CupSettingDataList, null);
		Instrument.SetHardwareParameters (cupData, subCupData);
		bool success = Instrument.RequestCupConfigurationChange (Instrument.CupConfigurationDataList);
		if (!success) {
			Logger.Log (LogLevel.UserError, "Could not request a cup configuration change.");
			return false;
		}
		return true;
	}
	
	
	
//====================================================================================================================================
//Server Methods
//====================================================================================================================================

    private static void TCPServeForever ()
    {
        Logger.Log (LogLevel.UserInfo, "Starting TCP Server.");
        TCPLISTENER = new TcpListener (IPAddress.Any, Config.port);
        TCPLISTENER.Start ();


        SERVER_THREAD = new Thread (new ThreadStart (TCPListen));
        SERVER_THREAD.Start ();

    }
    private static void UDPServeForever()
    {
        Logger.Log (LogLevel.UserInfo, "Starting UDP Server.");
        SERVER_THREAD = new Thread (new ThreadStart (UDPListen));
        SERVER_THREAD.Start ();

    }
    private static void UDPListen()
    {
        Logger.Log (LogLevel.UserInfo, "UDP Listening.");
        int recv;
        byte[] data= new byte[1024];

        IPEndPoint ipep = new IPEndPoint(IPAddress.Any, Config.port);
        UDP_SOCK = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        UDP_SOCK.Bind(ipep);

        IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        EndPoint remote = (EndPoint)(sender);

        while(true)
        {
            try
            {
                recv=UDP_SOCK.ReceiveFrom(data, ref remote);
                string rdata = Encoding.ASCII.GetString(data,0, recv);

                string result = ParseAndExecuteCommand(rdata.Trim());

                //Logger.Log(LogLevel.Debug, String.Format("Sending back {0}", result));
                UDP_SOCK.SendTo(Encoding.ASCII.GetBytes(result), remote);
            } catch (Exception e) {
            string error = string.Format ("Could not read from UDP sock. {0}", e.ToString ());
            Logger.Log(LogLevel.Warning, error);
            }

        }
    }
    private static void TCPListen ()
    {

        Logger.Log (LogLevel.UserInfo, "TCP Listening");

        while (true) {
            TCPHandle(TCPLISTENER.AcceptTcpClient ());

            //TcpClient client = TCPLISTENER.AcceptTcpClient ();
            //Thread clientThread = new Thread (new ParameterizedThreadStart (TCPHandle));
            //clientThread.Start (client);
        }
    }

    private static void TCPHandle (TcpClient tcpClient)
    {
        //TcpClient tcpClient = (TcpClient)client;

        NetworkStream _stream = tcpClient.GetStream ();
        //string response = Read (_stream);

        string result = ParseAndExecuteCommand (TCPRead(_stream).Trim());

        TCPWrite(_stream, result);

        tcpClient.Close();
    }
    //====================================================================================================================================
    //Network Methods
    //====================================================================================================================================

    private static void TCPWrite (NetworkStream stream, string cmd)
    {
        if (stream.CanWrite) {
            Byte[] sendBytes = Encoding.UTF8.GetBytes (cmd);
            stream.Write (sendBytes, 0, sendBytes.Length);
        }
    }

    private static string TCPRead (NetworkStream stream)
    {
        int BufferSize = 1024;
        byte[] data = new byte[BufferSize];
        try {
            StringBuilder myCompleteMessage = new StringBuilder ();
            int numberOfBytesRead = 0;

            // Incoming message may be larger than the buffer size.
            do {
                numberOfBytesRead = stream.Read (data, 0, data.Length);

                myCompleteMessage.AppendFormat ("{0}", Encoding.ASCII.GetString (data, 0, numberOfBytesRead));
            } while (stream.DataAvailable);

            //string rd = string.Format ("Read Data: {0} the received text is '{1}'", numberOfBytesRead, myCompleteMessage);

            return myCompleteMessage.ToString ();
        } catch (Exception e) {
            string error = string.Format ("Could not read from the NetworkStream. {0}", e.ToString ());
            Logger.Log(LogLevel.Warning, error);
        }
        return string.Empty;
    }


    //====================================================================================================================================
    //Helper Methods
    //====================================================================================================================================
    private static void mReportGains()
    {
        foreach (string d in DETECTOR_NAMES)
        {
            string dd=d.Split(',')[1];
            log(String.Format("Gain {0}={1}", dd, GetGain(dd)));
        }
    }

    private static void log(string msg)
    {
        Logger.Log(LogLevel.UserInfo, msg);
    }

    private static void listattributes(Type t, string identifier)
    {   Logger.Log(LogLevel.UserInfo, "List detector attributes");
        //Type t = item.GetType();
        //PropertyInfo[] pia = t.GetProperties();
        //Logger.Log(LogLevel.UserInfo, String.Format("GammaCor={0}", item.GammaCorrection));
        //foreach (PropertyInfo pi in pia)
        //{
        //  Logger.Log(LogLevel.UserInfo, String.Format("Name={0},Property {1}", item.Identifier,
        //          pi.ToString()));
        //}
        MemberInfo[] ms = t.GetMembers();
        foreach (MemberInfo mi in ms)
        {
        Logger.Log(LogLevel.UserInfo, String.Format("Name={0}, Member {1}",identifier,
                    mi.ToString()));
        }
        /*IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
        foreach (IRMSBaseCollectorItem item in cupData.CollectorItemList)
        {   Type t = item.GetType();
            PropertyInfo[] pia = t.GetProperties();
            //Logger.Log(LogLevel.UserInfo, String.Format("GammaCor={0}", item.GammaCorrection));
            foreach (PropertyInfo pi in pia)
            {
                Logger.Log(LogLevel.UserInfo, String.Format("Name={0},Property {1}", item.Identifier,
                        pi.ToString()));
            }
            MemberInfo[] ms = t.GetMembers();
            foreach (MemberInfo mi in ms)
            {
            Logger.Log(LogLevel.UserInfo, String.Format("Name={0}, Member {1}", item.Identifier,
                        mi.ToString()));
            }
        }*/
    }

    private static string mGetDetectorIdentifier(string name)
    {
        string ret="";
        foreach (string detname in DETECTOR_NAMES)
        {
            string[] args=detname.Split(',');
            if(args[1]==name)
            {
                ret=args[0];
                break;
            }
        }
        return ret;
    }

    //adapted from http://www.dotnetperls.com/array-slice
    private static string[] Slice(string[] source, int start, int end)
    {
    // Handles negative ends.
    if (end <= 0)
    {
        end = source.Length + end;
    }
    int len = end - start;

    // Return new array.
    string[] res = new string[len];
    for (int i = 0; i < len; i++)
    {
        res[i] = source[i + start];
    }
    return res;
    }
}


