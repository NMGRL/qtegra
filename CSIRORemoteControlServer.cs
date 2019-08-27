// RegisterSystemAssembly: System.dll 
// RegisterSystemAssembly: System.Core.dll
// RegisterSystemAssembly: System.Drawing.dll
// RegisterSystemAssembly: System.XML.dll
// RegisterSystemAssembly: System.XML.Linq.dll
// RegisterAssembly: DefinitionsCore.dll
// RegisterAssembly: BasicHardware.dll
// RegisterAssembly: PluginManager.dll
// RegisterAssembly: HardwareClient.dll
// RegisterAssembly: SpectrumLibrary.dll
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

//TODO: Check that all strings which use {0} also have the format command

__version__=2.1.1


Modified by Axel Suckow, Alec Deslandes, Christoph.Gerber (2019)

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
using System.Drawing;
using System.Linq;
using Thermo.Imhotep.Util.SpreadSheetML;

class CSIRORemoteControl
{
	// peak center setting variables
	private static double PCSetMassRangePercent=1.5;
	private static double PCSetValidIntensity=50;
	private static double PCSetUserPeakOffset;
	private static double PCPeakCenterResultMassOffset;

	InsertScript: Scripts\SampleFunctionIncludeScript.cs		

	private static int m_PORT = 1069;
	
	private static Socket UDP_SOCK;
	private static Thread SERVER_THREAD;
	private static TcpListener TCPLISTENER;

	private static bool USE_UDP=false;

	private static bool USE_BEAM_BLANK=true;
	private static bool peakCenterSuccessFull=false;

	private static bool MAGNET_MOVING=false;
	private static double MAGNET_MOVE_THRESHOLD=0.25;// threshold to trigger stepped magnet move in DAC units
	private static int MAGNET_STEPS=20; // number of steps to divide the total magnet displacement
	public static int MAGNET_STEP_TIME=100; //ms to delay between magnet steps
	private static double MAGNET_MOVE_THRESHOLD_REL_MASS = 0.4; //relative mass threshold to trigger stepped magnet move

	private static double LAST_Y_SYMMETRY=0;
	private static bool IsBLANKED=false;
	private static Dictionary<string, double> m_defl = new Dictionary<string, double>();
	private static LogLevel SCRIPT_ERROR_LOG_LEVEL = LogLevel.UserInfo; //Determines on what level script errors are logged, typically LogLevel.UserError or LogLevel.UserInfo

	public const int ON = 1;
	public const int OFF = 0;

	public const int OPEN = 1;
	public const int CLOSE = 0;
	
	public static IRMSBaseCore Instrument;
	public static IRMSBaseMeasurementInfo m_restoreMeasurementInfo;
	public static bool CDDS_ARE_ON = false; // keeps track of CDD state as long as this is done through "ActivateAllCDDs" and "DeactivateAllCDDs"

	public static string SCAN_DATA;
	public static object m_lock=new object();
	public static int commandCount = 0;

    private static List<string> DETECTOR_NAMES = new List<string>(new string[]{"CUP 4,H2",
    															  "CUP 3,H1",
																  "CUP 2,AX",
																  "CUP 1,L1",
																  "CUP 0,L2",
																  "CDD 0,L2 (CDD)",
																  "CDD 1,L1 (CDD)",
																  "CDD 2,AX (CDD)",
																  "CDD 3,H1 (CDD)",
																  "CDD 4,H2 (CDD)"
																  });


	public static void Main ()
	{
		Instrument= HelixMC;	
		
//		// Prints all properties and methods of PeakCenterSetting
//		IRMSBaseCupConfigurationData activeCupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
//		IRMSBasePeakCenterSetting pcSetting = activeCupData.PeakCenterSetting;
//		log(typeof(IRMSBasePeakCenterSetting).ToString());
//		log(typeof(IRMSBasePeakCenterSetting).GetFields().ToString());
//		log("Properties of PeakCenterSetting:");
//		PropertyInfo[] piArray = typeof(IRMSBasePeakCenterSetting).GetProperties();
//		log(piArray.Length.ToString());
//		foreach (PropertyInfo pi in piArray)
//		{
//			log(pi.Name.ToString());
//		}
		
//		log("Methods of PeakCenterSetting:");
//		MethodInfo[] miArray = typeof(IRMSBasePeakCenterSetting).GetMethods();
//		log(miArray.Length.ToString());
//		foreach (MethodInfo mi in miArray)
//		{
//			log(mi.Name.ToString());
//		}
		
		
		//init parameters
		Instrument.GetParameter("Y-Symmetry Set", out LAST_Y_SYMMETRY);
		
		GetTuningSettings();
		PrepareEnvironment();
		
		//mReportGains();
	
		//list attributes
		//listattributes(Instrument.GetType(), "instrument");
		
        //setup data recording
		InitializeDataRecord();

		
		if (USE_UDP)
		{
			UDPServeForever();
		}
		else
		{
		 	TCPServeForever();
		}
	}
	
    //====================================================================================================================================
	//	Last Update: 18 June 2019 
	//
	//	Commands are case sensitive and in CamelCase.
	//  Inputs are given in <> (do not include the "<" or ">" in the commands). 
	//  e.g SetTrapVoltage 120 not SetTrapVoltage <120>
	//	Output is generally a short string stating whether the command was
	//	executed successfully (e.g. "OK", "Error: TrapVoltage could not be set")
	//	More complex output is indicated with a -> sign
	//
	//	Commands that have been tested and found to work at CSIRO are marked 
	//	with an @.
	//
	//	Commands:
	//		GetParameters
	//		GetTuningSettingsList -> (a comma separated string)
	//		SetTuningSettings
	//		@GetData -> (tagged data e.g. H2,mass,intensity,L1 (CDD),mass,intensity,...)
	//		@SetIntegrationTime <seconds>
	//      @GetIntegrationTime -> seconds
	// 		BlankBeam <true or false> if true set y-symmetry to -50 else return to previous value
    
    //===========Cup/SubCup Configurations==============================
    // 		GetCupConfigurationList
	//		GetSubCupConfigurationList
	//		GetActiveCupConfiguration
	//		GetActiveSubCupConfiguration
	// 		GetSubCupParameters -> (list of Deflection voltages and the Ion Counter supply voltage)
	//		SetSubCupConfiguration <sub cup name>
	//      @ActivateCupConfiguration <cup name>:<sub cup name>
	//
	//===========Ion Pump Valve=========================================
    //      @Open  #opens the Ion pump to the mass spec
	//		@Close #closes the Ion pump to the mass spec
	//		GetValveState #returns True for open false for closed
	
	//===========Magnet=================================================
	//		GetMagnetDAC
	//		SetMagnetDAC <value> #0-10V
	//      GetMagnetMoving
	//      @SetMass <value>,<string cupName> #deactivates the CDDs if they are active
	//		@GetMass -> Mass on the AX cup
	
	//===========Source=================================================
	//		GetHighVoltage or GetHV
	//		SetHighVoltage or SetHV <kV>
	//      GetTrapVoltage
	//      SetTrapVoltage <value>
    //      GetElectronEnergy
    //      SetElectronEnergy <value>
    //		GetIonRepeller
    //		SetIonRepeller <value>
    //      GetYSymmetry
    //      SetYSymmetry <value>
    //      GetZSymmetry
    //      SetZSymmetry <value>
    //      GetZFocus
    //      SetZFocus <value>
    //      GetExtractionFocus
    //      SetExtractionFocus <value>
    //      GetExtractionSymmetry
    //      SetExtractionSymmetry <value>
    //      GetExtractionLens
    //      SetExtractionLens <value>
    
    //==========Detectors===============================================
    //      @ActivateAllCDDs #activates and unprotects all CDDs with a mass in the CupConfiguration
	//      @DeactivateAllCDDs #deactivates and protects all CDDs
	//		@ActivateCDD <name>
	//		ProtectDetector <name>,<On/Off>
	//		GetDeflections <name>[,<name>,...]
	//      GetDeflection <name>
	//      SetDeflection <name>,<value>
	//      GetCDDVoltage
	//      SetCDDVoltage <value>
	//      GetGain <name>
	//		SetGain <name>,<value>
	//
	//==========Operations=================================================
	//		@PeakCenter <cupName>,<peakMass>[,<setMass>] #the last is optional, defines mass at which scan is continued after the peak center
	//		PeakCenterGetResult
	//
    //==========Generic Device===============================================
	//      GetParameters <name>[,<name>,...]
	//      GetParameter <name> -  name is any valid device currently listed in the hardware database
	//      SetParameter <name>,<value> -  name is any valid device currently listed in the hardware database
	//
	//==================================================================
	//		Error Responses:
	//			Error: Invalid Command   - the command is poorly formated or does not exist. 
	//			Error: could not set <hardware> to <value> 
	//
	//====================================================================================================================================
	
	private static string ParseAndExecuteCommand (string cmd)
	{
		commandCount = commandCount + 1;
		log(commandCount.ToString());
		string result = "Error: Invalid Command";
		bool ParResult = false;
		Logger.Log(LogLevel.UserInfo, String.Format("{0}: Executing {1}",commandCount.ToString(), cmd));
		
		string[] args = cmd.Trim().Split (' ');
		string[] pargs;
		string jargs;

		double r;
		switch (args[0]) {

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
			};
			break;
			
		case "GetData":
			result=SCAN_DATA;
			break;
	
		case "SetIntegrationTime":
			result=SetIntegrationTime(Convert.ToDouble(args[1]));
			break;

		case "PeakCenterSetMassRangePercent":
			PCSetMassRangePercent=Convert.ToDouble(args[1]);
			Instrument.PeakCenterSetting = Instrument.CupConfigurationDataList.GetActiveCupConfiguration().PeakCenterSetting;
			Instrument.PeakCenterSetting.MassRangePercent = PCSetMassRangePercent;
			result="OK";
			break;
			
		case "PeakCenterSetValidIntensity":
			PCSetValidIntensity=Convert.ToDouble(args[1]);
			Instrument.PeakCenterSetting = Instrument.CupConfigurationDataList.GetActiveCupConfiguration().PeakCenterSetting;
			Instrument.PeakCenterSetting.ValidIntensity = PCSetValidIntensity;
			result="OK";
			break;

		case "PeakCenterGetUserOffset":
			result=Convert.ToString(PCSetUserPeakOffset);;
			break;
			
		case "PeakCenterGetResultOffset":
			result=Convert.ToString(PCPeakCenterResultMassOffset);;
			break;
			
		case "GetIntegrationTime":
			result=GetIntegrationTime();
			break;
			
		case "BlankBeam":	
			ParResult = SwitchBlankBeam(args[1].ToLower());
			if(ParResult)
			{ result="OK"; }
			else
			{ result="Error: could not blank the beam";}
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
			
		case "SetSubCupConfiguration":
//			Logger.Log(LogLevel.Debug, String.Format("Set SupCup {0}",cmd));
//			if(ActivateCupConfiguration ("Argon", cmd.Remove(0,23)))
//			{
//				result="OK";
//			}
//			else
//			{
//				result=String.Format("Error: could not set sub cup to {0}", args[1]);
//			}
//			break;
			jargs=Instrument.CupConfigurationDataList.GetActiveCupConfiguration().Name;
            result=mActivateCup(jargs, args[1]);
			break;

		case "ActivateCupConfiguration":
			jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(':');
		    result = mActivateCup(pargs[0], pargs[1]);
            break;

			
//============================================================================================
//   Ion Pump Valve
//============================================================================================					
        case "Open":
//			jargs=String.Join(" ", Slice(args,1,0));
//			ParResult=SetParameter(jargs,OPEN);
			if (SetParameter("Valve Ion Pump Set", 1))
			{ result="Valve opened"; }
			else
			{ result=String.Format("Error: could not open the valve"); }
			break;
			
		case "Close":
//			jargs=String.Join(" ", Slice(args,1,0));
//			ParResult=SetParameter(jargs,CLOSE);
			if (SetParameter("Valve Ion Pump Set", 0))
			{ result="Valve closed"; }
			else
			{ result=String.Format("Error: could not close the valve"); }
			break;
			
		case "GetValveState":
			result=GetValveState("Valve Ion Pump Set");
			break;

//============================================================================================
//   Magnet
//============================================================================================					
		case "GetMagnetDAC":
			if (Instrument.GetParameter("Field Set", out r))
			{
				result=r.ToString();
			}
			break;
			
		case "SetMagnetDAC":		
			result=SetMagnetDAC(Convert.ToDouble(args[1]));
			break;
			
		case "GetMagnetMoving":
		    result=GetMagnetMoving();
			break;
			
		case "SetMass":
			jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(',');
		    result = SetMass(Convert.ToDouble(pargs[0]),(pargs.Length>1) ? pargs[1] : null);
		    break;
		    
		case "GetMass":
			result = HelixMC.MeasurementInfo.ActualScanMass.ToString();
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
			ParResult=SetParameter("Acceleration Reference Set", Convert.ToDouble(args[1])/1000.0);
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set high voltage"); }
			break;
			
		case "GetHV":
			if(Instrument.GetParameter("Acceleration Reference Set",out r))
			{
				result=(r).ToString();
			}
			break;
		case "SetHV":
			ParResult=SetParameter("Acceleration Reference Set", Convert.ToDouble(args[1])/1000.0);
			break;
			
		case "GetTrapVoltage":
			if(Instrument.GetParameter("Trap Voltage Readback",out r))
			{
				result=r.ToString();
			}
			break;
			
		case "SetTrapVoltage":
			ParResult=SetParameter("Trap Voltage Set",Convert.ToDouble(args[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set Trap Voltage"); }
			break;
			
		case "GetElectronEnergy":
			if(Instrument.GetParameter("Electron Energy Readback",out r))
			{
				result=r.ToString();
			}
			break;
			
		case "SetElectronEnergy":
			ParResult=SetParameter("Electron Energy Set",Convert.ToDouble(args[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set Electron Energy"); }
			break;
			
		case "GetIonRepeller":
			if(Instrument.GetParameter("Ion Repeller Set",out r))
			{
				result=r.ToString();
			}
			break;
			
		case "SetIonRepeller":
			ParResult=SetParameter("Ion Repeller Set",Convert.ToDouble(args[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set Ion Repeller"); }
			break;
		
		case "GetYSymmetry":
			if(Instrument.GetParameter("Y-Symmetry Set",out r))
			{
				result=r.ToString();
			}
			break;
			
		case "SetYSymmetry":
			LAST_Y_SYMMETRY=Convert.ToDouble(args[1]);
			ParResult=SetParameter("Y-Symmetry Set",Convert.ToDouble(args[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set Y Symmetry"); }
			break;
		
		case "GetZSymmetry":
			if(Instrument.GetParameter("Z-Symmetry Set",out r))
			{
				result=r.ToString();
			}
			break;
			
		case "SetZSymmetry":
			ParResult=SetParameter("Z-Symmetry Set",Convert.ToDouble(args[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set Z Symmetry"); }
			break;
			
		case "GetZFocus":
			if(Instrument.GetParameter("Z-Focus Set",out r))
			{
				result=r.ToString();
			}
			break;
		
		case "SetZFocus":
			ParResult=SetParameter("Z-Focus Set",Convert.ToDouble(args[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set Z Focus"); }
			break;

		case "GetExtractionFocus":
			if(Instrument.GetParameter("Extraction Focus Set",out r))
			{
				result=r.ToString();
			}
			break;

		case "SetExtractionFocus":
			ParResult=SetParameter("Extraction Focus Set",Convert.ToDouble(args[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set Extraction Focus"); }
			break;

		case "GetExtractionSymmetry":
			if(Instrument.GetParameter("Extraction Symmetry Set",out r))
			{
				result=r.ToString();
			}
			break;

		case "SetExtractionSymmetry":
			ParResult=SetParameter("Extraction Symmetry Set",Convert.ToDouble(args[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set Extraction Symmetry"); }
			break;

		case "GetExtractionLens":
			if(Instrument.GetParameter("Extraction Lens Set",out r))
			{
				result=r.ToString();
			}
			break;			
			
		case "SetExtractionLens":
			ParResult=SetParameter("Extraction Lens Set",Convert.ToDouble(args[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set Extraction Lens"); }
			break;
		
//============================================================================================
//    Detectors
//============================================================================================			
				
		case "ActivateAllCDDs":
		    result = ActivateAllCDDs();
			break;
			
		case "DeactivateAllCDDs":
		    result = DeactivateAllCDDs();
			break;      
			
		case "ActivateCDD":
			jargs=String.Join(" ", Slice(args,1,0));
		    result = ActivateCDD(jargs);
			break;

		case "ProtectDetector":
//        	jargs=String.Join(" ", Slice(args,1,0));
//            pargs=args[1].Split(',');
//            pargs=jargs.Split(',');
//        	jargs=String.Join(" ", Slice(args,1,0));
            pargs=args[1].Split(',');
            ProtectDetector(pargs[0], pargs[1]);
            result="OK";
            break;
            
        case "GetDeflections":
            result = GetDeflections(args);
            break;
            
        case "GetDeflection":
            jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(',');
            if(Instrument.GetParameter(String.Format("Deflection {0} Set",pargs[0]), out r))
            {
                result=r.ToString();
            }
            break;     
            
        case "SetDeflection":
            jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(',');
			ParResult=SetParameter(String.Format("Deflection {0} Set",pargs[0]),Convert.ToDouble(pargs[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not Set Deflection"); }
		    break;	
		    
		case "GetCDDVoltage":
            jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(',');
            if(Instrument.GetParameter(String.Format("{0} Supply Set",pargs[0]), out r))
            {
                result=r.ToString();
            }
            break;   
            
        case "SetCDDVoltage":
            jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(',');
			jargs=pargs[0] + " Supply Set";
        	ParResult=SetParameter(jargs, Convert.ToDouble(pargs[1]));
			if (ParResult)
			{ result="OK"; }
			else
			{ result=String.Format("Error: could not set Ion Counter Voltage"); }
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
//    Operations
//============================================================================================			
		case "PeakCenter":
			jargs=String.Join(" ", Slice(args,1,0));
            pargs=jargs.Split(',');
		    result="Wait For Peak Center!";
			if (!RunPeakCentering(pargs[0],Convert.ToDouble(pargs[1]),(pargs.Length>2) ? Convert.ToDouble(pargs[2]) : Convert.ToDouble(pargs[1])))
			{
				result = "Error: Could not do peak center.";
				peakCenterSuccessFull = false;
				break;
			}
			peakCenterSuccessFull = true;
			result = "OK";
		    break;
		case "PeakCenterGetResult":
			result=peakCenterSuccessFull.ToString();
		    break;

//============================================================================================
//    Generic
//============================================================================================			
		case "GetParameters":
            result = GetParameters(args);
            break;
            
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
            ParResult=SetParameter(pargs[0], Convert.ToDouble(pargs[1]));
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
    
    
    ///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Prepares the environment for a peak center by 1) turning off the CDDs, 2) 
	/// changing the mass, 3) turning the CDDs back on if peak center is run on
	/// a CDD.
	/// </summary>
	/// <param name="cupName">The cup configuration name.</param>
	/// <param name="cupMass">The the mass for which peak centering is done.</param>
	/// <returns><c>True</c> if successfull; otherwise <c>false</c>.</returns>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static bool PrepareEnvironmentForPeakCenter(string cupName, double cupMass)
    {
        Logger.Log(LogLevel.UserInfo,"Preparing environment for a peak center...");
        
        if (SetMass(cupMass,cupName)!= "OK") //Note: SetMass automatically deactivates CDDs
        {
        	Logger.Log(SCRIPT_ERROR_LOG_LEVEL, "Error: could not set mass to desired value. Aborting peak center...");
        	return false;
        }
        
        if (IsCDD(cupName)) // Activate CDDs if needed (i.e. if peak center was requested on a CDD)
        {
        	if (ActivateAllCDDs()!= "OK")
        	{
        		Logger.Log(SCRIPT_ERROR_LOG_LEVEL, "Error: could not reactivate CDDs. Aborting peak center...");
        		return false;
        	}
        }
        m_restoreMeasurementInfo=Instrument.MeasurementInfo;
        
        return Instrument.ScanTransitionController.InitializeScriptScan(m_restoreMeasurementInfo);
    }  
    
    ///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Determines whether a given cup is a CDD
	/// </summary>
	/// <param name="cupName">The cup configuration name.</param>
	/// <returns><c>True</c> if the cup is a CDD; otherwise <c>false</c>.</returns>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static bool IsCDD(string cupName)
    {
    	bool result = false;
    	IRMSBaseCupConfigurationData activeCupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
    	
    	foreach(IRMSBaseCollectorItem col in activeCupData.CollectorItemList)
		{
			if ((col.CollectorType == IRMSBaseCollectorType.CounterCup) && (col.Appearance.Label == cupName))
			{
			    result = true;
			}
		}
		return result;
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
		if (USE_UDP)
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
    public static string GetParameters(string[] args)
    {
        List<string> data = new List<string>();
        double v;
        string param;
        foreach(string k in args[1].Split(','))
        {
           param="";
           switch (k) {
           case "YSymmetry":
                param="Y-Symmetry Set";
                break;
           case "ZSymmetry":
                param="Extraction-Symmetry Set";
                break;
           case "HighVoltage":
                param="Acceleration Reference Set";
                break;
           case "HV":
                param="Acceleration Reference Set";
                break;
		   case "TrapVoltage":
                param="Trap Voltage Readback";
		        break;
	       case "ElectronEnergy":
                param="Electron Energy Readback";
	            break;
           case "ZFocus":
                param="Extraction-Focus Set";
                break;
           case "IonRepeller":
                param="Ion Repeller Set";
                break;
           case "ExtractionFocus":
                param="Extraction Focus Set";
                break;
           case "ExtractionSymmetry":
                param="Extraction Symmetry Set";
                break;
           case "ExtractionLens":
                param="Extraction Lens Set";
                break;
           }
           //log(param);
		   //log(k);
           if (param != "")
           {
               Instrument.GetParameter(param, out v);
           }
           else
           {
                v = 0;
           }
           data.Add(v.ToString());
        }
        return string.Join(",",data.ToArray());
    }
    
    public static bool SwitchBlankBeam(string switchOn)
    {
    		if (!USE_BEAM_BLANK)
			{	
				return true;
			}
			
			double yval=LAST_Y_SYMMETRY;
			bool blankbeam=false;
			if (switchOn=="true")
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
					if(!SetParameter("Y-Symmetry Set",yval))
					{return false;}
				}
			}			

			if(blankbeam)
			{
				if(!SetParameter("Y-Symmetry Set",yval))
				{return false;}
			};
			return true;
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
	    string param=String.Format("Deflection {0} CDD Set",detname);
	    if (state.ToLower()=="on")
	    {
	    	double v;
	        Instrument.GetParameter(param, out v);
	        m_defl[detname]=v;
	        SetParameter(param, 3250);
	    }
	    else
	    {
	        if (m_defl.ContainsKey(detname))
	        {
	            SetParameter(param, m_defl[detname]);
	        }
	        else
	        {
	        	SetParameter(param, 0);
	        }
	    }
	}
	
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Activate all CDD cups with a mass in the cupconfiguration. After the
	/// activation, a monitor scan is resumed at the same mass as before.
	/// </summary>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static string ActivateAllCDDs()
	{				
		// activate the CDDs...
		if (!ActivateAllCounterCups(true))
		{
		    return "Could not activate CDDs.";
		}
		Thread.Sleep(1000); // Wait for a little time for the activation to take effect...

		// Restart the monitor scan, to ensure data is reported for the CDD...
		if (!RunMonitorScan(HelixMC.MeasurementInfo.ActualScanMass))
		{
			return "Could not restart monitor scan.";
		}
		
		return "OK";
	}
	
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Deactivate all CDD cups with a mass in the cupconfiguration. The
	/// monitor scan is then resumed at the actual mass.
	/// </summary>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static string DeactivateAllCDDs()
	{	

		if (!ActivateAllCounterCups(false))
		{
			return "Could not deactivate CDDs.";
		}

		//This ensures that after the deactivation, data is not reported for the CDD...
		if (!RunMonitorScan(HelixMC.MeasurementInfo.ActualScanMass))
		{
			return "Could not restart monitor scan.";
		}
		Thread.Sleep(1000); //need to wait for a second before changes are done
		
		return "OK";
	}
		
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Activate a specific CDD cup. After the
	/// activation, a monitor scan is resumed at the same mass as before.
	/// </summary>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static string ActivateCDD(string cupName)
	{				
		// activate the CDDs...
		if (!ActivateCounterCup(true, cupName))
		{
		    return "Could not activate CDDs.";
		}
		Thread.Sleep(1000); // Wait for a little time for the activation to take effect...

		// Restart the monitor scan, to ensure data is reported for the CDD...
		if (!RunMonitorScan(HelixMC.MeasurementInfo.ActualScanMass))
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
	
//	public static string SetParameter(string hwname, int val)
//	{
//		string result="OK";
//		if (!Instrument.SetParameter(hwname, val))
//		{
//			result=String.Format("Error: could not set {0} to {1}", hwname, val);
//		}
//		return result;
//	}
//	public static string SetParameter(string hwname, double val)
//	{	string result="OK";
//	
//	
//		if (!Instrument.SetParameter(hwname, val))
//		{
//			result=String.Format("Error: could not set {0} to {1}", hwname, val);
//		}
//		return result;
//	}
	
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Set the mass to a certain value. CDDs get deactivated before the mass is
	/// changed. For large relative mass changes it is done stepwise to avoid
	//	ringing
	/// </summary>
	/// <param name="m">The mass to which the central cup should be set.</param>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
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
		double initialMass = HelixMC.MeasurementInfo.ActualScanMass;
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
		bool ParResult=false;
		double current_dac;
		
		// First deactivate CDDs to protect them.
		if (!(DeactivateAllCDDs()=="OK"))
		{ 
			return "Error: Could not deactivate CDDs. Mass not changed.";
		}
		
		if (Instrument.GetParameter("Field Set", out current_dac))
		{
			double dev=Math.Abs(d-current_dac);			
			if (dev>MAGNET_MOVE_THRESHOLD)
			{   
                Thread t= new Thread(delegate(){mSetMagnetDAC(d,dev,current_dac);});
                t.Start();
                ParResult=true;
			}
			else
			{
				ParResult=SetParameter("Field Set", d);
			}
		}
		if (ParResult){	return "OK"; } else { return "Error: could not set Magnet Dac"; }
	}
	
	public static void mSetMagnetDAC(double d, double dev, double current_dac)
	{
		MAGNET_MOVING=true;
	    //incrementally move the magnet to eliminate "ringing"
	    double step=dev/MAGNET_STEPS;
	    int sign=1;

        if (current_dac>d)
        {
            sign=-1;
        }

        for(int i=1; i<=MAGNET_STEPS; i++)
        {
            SetParameter("Field Set", current_dac+sign*i*step);
            if (MAGNET_STEP_TIME>0)
            {
                Thread.CurrentThread.Join(MAGNET_STEP_TIME);
            }
        }
        MAGNET_MOVING=false;
	}
	public static string GetIntegrationTime()
	{
		string result=String.Format("{0}", Instrument.MeasurementInfo.IntegrationTime*0.001);
		return result;
	}
	
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Changes the integration time of the monitoring scan.
	/// </summary>
	/// <param name="t">The integration time to be set. If it is not a valid integration time, the next largest valid integration time will be chosen.</param>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	public static string SetIntegrationTime(double t)
	{
				
		// Check that integration time is valid
		t = ConvertToValidIntegrationTime(t);
		
		
		//t in ms	
		string result="OK";
		IRMSBaseMeasurementInfo nMI= new IRMSBaseMeasurementInfo(Instrument.MeasurementInfo);
		nMI.IntegrationTime = t*1000;

		
		if (!Instrument.ScanTransitionController.StartMonitoring (nMI))
		{
			Logger.Log(SCRIPT_ERROR_LOG_LEVEL, "ERROR: Could not start the modified monitor");
			result=String.Format("Error: could not set integration time to {0}",t);
		}
		
		return result;
	}
	
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Changes the peak center settings of the active cup configuration.
	/// </summary>
	/// <param name="t">The Setiting to be set.</param>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	public static string SetPeakCenterSetting(byte What, double t)
	{
		//t in ms	
		string result="Did Not Work";
		IRMSBasePeakCenterSetting pcSetting = Instrument.CupConfigurationDataList.GetActiveCupConfiguration().PeakCenterSetting;
		switch (What) {
			case 1:
				pcSetting.PeakSignificantHeightPercent=t;
				result = "OK";
				break;
			case 2:
				pcSetting.MassRangePercent=t;
				result = "OK";
				break;
		}
		return result;
	}
	
	
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Returns a valid integration time for the monitoring scan.
	/// </summary>
	/// <param name="t">The desired integration time. If it is not a valid integration time, the next largest valid integration time will be chosen.</param>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static double ConvertToValidIntegrationTime(double time)
	{
		double integrationTimeMin = 0.131072;
		int integrationTimeMaxPower = 8; // corresponds to 67.108864 seconds, which is the largest valid integration time
		double tempTime = time;
		int counter = 0;
		while ((tempTime > integrationTimeMin) && (counter <= integrationTimeMaxPower))
		{
			tempTime = tempTime/2;
			counter++;
		}
		double result = integrationTimeMin*Math.Pow(2,counter);
		Logger.Log(LogLevel.UserInfo, String.Format("Requested integration time was {0}, effective integration time is {1}.",time,result));
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
			
			// Loop through all detectors
			foreach (string detname in DETECTOR_NAMES)
        	{
        		string[] args=detname.Split(',');
        		string cupName = args[1];
        		bool nameWasFound = false;
        		
        		
        		// Loop through collectors in active CupConfiguration to check
        		// whether detector is in the active cupConfiguration
        		foreach (IRMSBaseCollectorItem item in cupData.CollectorItemList)
				{
        			if(cupName==item.Appearance.Label)
        			{
        				// Loop through all the data to find the data point
        				// which has the same mass
        				foreach (Series series in spec)
						{
							foreach (SpectrumData point in series)
							{
								if (Math.Abs(GetActualMass(cupName)-point.Mass)<0.0001)
								{
									data.Add(cupName);
									data.Add(point.Mass.ToString());
									data.Add(point.Analog.ToString());
									nameWasFound = true;
								}
							}
						} 
					}
				}
				if (!nameWasFound)
				{
					data.Add(cupName);
					data.Add("999999999");
					data.Add("999999999");
				}
			}

			SCAN_DATA=string.Join(",",data.ToArray());
		}
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
			Logger.Log(SCRIPT_ERROR_LOG_LEVEL, String.Format("Could not load tune setting \'{0}\'.", name));
			return false;
		}
		else
		{
			tuneBlock = tuneSettings.Object as TuneParameterBlock;
			if (tuneBlock == null)
			{
				Logger.Log(SCRIPT_ERROR_LOG_LEVEL, String.Format("Tune setting \'{0}\' could not convert to tuneblock.", name));
				return false;
			}
			else
			{
				Instrument.TuneParameters.Parameters = tuneBlock;
				Logger.Log(LogLevel.UserInfo, String.Format("Tune setting : \'{0}\' successfully load!", name));
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
			Logger.Log (SCRIPT_ERROR_LOG_LEVEL, String.Format ("Could not find cup configuration \'{0}\'.", cupConfigurationName));
			return false;
		}
		IRMSBaseSubCupConfigurationData subCupData = cupData.SubCupConfigurationList.FindSubCupConfigurationByName (subCupConfigurationName);
		if (subCupData == null) {
			Logger.Log (SCRIPT_ERROR_LOG_LEVEL, String.Format ("Could not find sub cup configuration \'{0}\' in cup configuration.", subCupConfigurationName, cupConfigurationName));
			return false;
		}
		Instrument.CupConfigurationDataList.SetActiveItem (cupData.Identifier, subCupData.Identifier, Instrument.CupSettingDataList, null);
		Instrument.SetHardwareParameters (cupData, subCupData);
		bool success = Instrument.RequestCupConfigurationChange (Instrument.CupConfigurationDataList);
		if (!success) {
			Logger.Log (SCRIPT_ERROR_LOG_LEVEL, "ERROR: Could not request a cup configuration change.");
			return false;
		}
		return true;
	}
	
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Running a peak center. 
	/// </summary>
	/// <param name="cupName">The cup configuration name.</param>
	/// <param name="peakMass">The the mass for which peak centering is done.</param>
	/// <param name="setMass">The the mass to which the instrument is set before and after the peak center.</param>
	/// <returns><c>True</c> if successfull; otherwise <c>false</c>.</returns>
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static bool RunPeakCentering (string cupName, double peakMass, double setMass)
	{
////		Stuff we tried to find out more about an object...
//		Instrument.GetCDDActivationMode();
//		MethodInfo[] methodInfos = Type.GetType("IRMSBaseCupConfigurationData").GetMethods();
//		Logger.Log (LogLevel.UserInfo, methodInfos.ToString());
//		Type.GetType("IRMSBaseCupConfigurationData").GetFields();
//		IList<PropertyInfo> props = new List<PropertyInfo>(activeCupData.GetFields());
//		foreach (PropertyInfo prop in props)
//		{
//    		object propValue = prop.GetValue(activeCupData, null);
//			Logger.Log (LogLevel.UserInfo, propValue.ToString());
//			Logger.Log (LogLevel.UserInfo, prop.GetName());
//		}

		if (!PrepareEnvironmentForPeakCenter(cupName, setMass))
		{
			Logger.Log (SCRIPT_ERROR_LOG_LEVEL, "ERROR: Could not prepare environment for peak centering. Aborting peak center...");
			return false;
		}
		
		if (!RunPeakCenter(cupName,peakMass))
		{
			Logger.Log (SCRIPT_ERROR_LOG_LEVEL, "ERROR: Could not do peak centering.");
			return false;
		}
		
		DeactivateAllCDDs();
		
//		if (!RestoreScanEnviroment()) 
//		{
//			Logger.Log (SCRIPT_ERROR_LOG_LEVEL, "ERROR: Could not do return to scanning after peak centering.");
//			return false;
//		}
			SetMass(peakMass,cupName);
		return true;
	}
	
	
//    ///-------------------------------------------------------------------------------------------------------------------------------------------------------------
//	/// <summary>
//	/// Restore the instrument to its old state.
//	/// </summary>
//	/// <returns><c>True</c> if the scan enviroment successfull restored; otherwise <c>false</c>.</returns>
//	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
//	private static bool RestoreScanEnviromentWithCDDs()
//	{
//		if (Instrument != null && m_restoreMeasurementInfo != null)
//		{
//			if (!Instrument.ScanTransitionController.StartMonitoring(m_restoreMeasurementInfo))
//			{
//				Logger.Log(SCRIPT_ERROR_LOG_LEVEL, "ERROR: Could not restart the previous monitor scan.");
//				return false;
//			}
//
//
//		m_restoreMeasurementInfo = null;
//		return true;
//	}
	
//====================================================================================================================================
//Server Methods
//====================================================================================================================================

	private static void TCPServeForever ()
	{
		Logger.Log (LogLevel.UserInfo, "Starting TCP Server.");
		TCPLISTENER = new TcpListener (IPAddress.Any, m_PORT);
		TCPLISTENER.Server.ReceiveTimeout = 600000;
		TCPLISTENER.Server.SendTimeout = 600000;
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
		
		IPEndPoint ipep = new IPEndPoint(IPAddress.Any, m_PORT);
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
			string error = String.Format ("Could not read from UDP sock. {0}", e.ToString ());
			Logger.Log(LogLevel.Warning, error);
			}
			
		}
	}
	
	private static TcpClient tcpClient;
	private static void TCPListen ()
	{
		
		Logger.Log (LogLevel.UserInfo, "TCP Listening");
		
		
		while (true) {
			try
        	{
				tcpClient = TCPLISTENER.AcceptTcpClient ();
				Logger.Log (LogLevel.UserInfo, "tcpClient accepted");
			}
			catch (Exception e)
        	{
            	Logger.Log (LogLevel.UserInfo, String.Format("SocketException: {0}", e.ToString()));
        	}
			TCPHandle(tcpClient);
			//TcpClient client = TCPLISTENER.AcceptTcpClient ();
			//Thread clientThread = new Thread (new ParameterizedThreadStart (TCPHandle));
			//clientThread.Start (client);
		}
	}
	
	private static void TCPHandle (TcpClient tcpClient)
	{
		//TcpClient tcpClient = (TcpClient)client;
		Logger.Log (LogLevel.UserInfo, "TCPHAndle started executing...");
		NetworkStream _stream = tcpClient.GetStream ();
		//string response = Read (_stream);
		
		string result = ParseAndExecuteCommand (TCPRead(_stream).Trim());
		Logger.Log (LogLevel.UserInfo, "Parse and execute command finished");
		
		TCPWrite(_stream, result);
		Logger.Log (LogLevel.UserInfo, "TCPWrite finished.");
		
		tcpClient.Close();
		Logger.Log (LogLevel.UserInfo, "TCP client requested to close.");
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
			string error = String.Format ("Could not read from the NetworkStream. {0}", e.ToString ());
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
	{	Logger.Log(LogLevel.UserInfo, "List detector attributes");
	    //Type t = item.GetType();
		//PropertyInfo[] pia = t.GetProperties();
		//Logger.Log(LogLevel.UserInfo, String.Format("GammaCor={0}", item.GammaCorrection));
		//foreach (PropertyInfo pi in pia)
		//{
		//	Logger.Log(LogLevel.UserInfo, String.Format("Name={0},Property {1}", item.Identifier,
		//			pi.ToString()));
		//}
		MemberInfo[] ms = t.GetMembers();
		foreach (MemberInfo mi in ms)
		{
		Logger.Log(LogLevel.UserInfo, String.Format("Name={0}, Member {1}",identifier,
					mi.ToString()));
		}
		/*IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
		foreach (IRMSBaseCollectorItem item in cupData.CollectorItemList)
		{	Type t = item.GetType();
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
    
    
    ///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Returns the actual mass of the selected cup.
	/// </summary>
	/// <param name="cupName">The name of the cup as string e.g. "L2 (CDD)".</param>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static double GetActualMass(string cupName)
	{
    	IRMSBaseCupConfigurationData activeCupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
    	
    	// find the cup
    	foreach(IRMSBaseCollectorItem col in activeCupData.CollectorItemList)
		{
			if ((col.Appearance.Label == cupName)&&col.Mass.HasValue)
			{
			    return col.GammaCorrection*HelixMC.MeasurementInfo.ActualScanMass;
			}
		}
		
		return 999999999;
    }
    
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	/// <summary>
	/// Class to read out collector infos from a cup / sub cup configuration.
	/// </summary>
	///-------------------------------------------------------------------------------------------------------------------------------------------------------------
	public class CollectorInfo
	{
		public CollectorInfo(string name, bool active, double? mass) 
		{
			Name = name;
			Active = active;
			Mass = mass;
		}
		public string Name { get; private set; }
		public bool Active { get; private set; }
		public double? Mass { get; private set; }
		public override string ToString()
		{
			return String.Format("Name: {0};Active: {1};Mass: {2}", Name, Active, Mass.HasValue ? Mass.Value.ToString(CultureInfo.InvariantCulture) : ""); 
		}
	}
	
	public class CupInfo
    {
        public CupInfo(string identifier, string name, double mass, double peakCenterMassOffsetResult, bool isCDD)
        {
            Identifier = identifier;
            Name = name;
            Mass = mass;
            PeakCenterMassOffsetResult = peakCenterMassOffsetResult;
            Intensities = new List<double>();
            Times = new List<double>();
            RelativeDeltaIntensities = new List<double>();
            CalculatedCupFactor = 1.0;
            IsCDD = isCDD;
            HasOutlier = false;
            OutlierIndexes = new List<int>();
        }
        public string Identifier { get; private set; }
        public string Name { get; private set; }
        public double Mass { get; private set; }
        public bool IsCDD { get; private set; }
        public double PeakCenterMassOffsetResult { get; set; }
        public List<double> Intensities { get; private set; }
        public List<double> Times { get; private set; }
        public List<double> RelativeDeltaIntensities { get; private set; }
        public double CalculatedCupFactor { get; set; }
        public bool HasOutlier { get; set; }
        public List<int> OutlierIndexes { get; set; }
    }
}


