#include "../stdafx.h"
#include "FAST.h"
#include "../named-var-set.h"
#include "../config.h"
#include "world.h"
#include "../reward.h"
#include "../app.h"
#include "../app-rlsimion.h"
#include "../../tools/WindowsUtils/Process.h"
#include <string>
#include <stdio.h>
#include "../app.h"

#define FAST_WIND_CONFIG_FILE "NRELOffshrBsline5MW_InflowWind.dat"
#define FAST_WIND_CONFIG_TEMPLATE_FILE "..\\config\\world\\FAST\\NRELOffshrBsline5MW_InflowWindTemplate.dat"
#define TURBSIM_TEMPLATE_CONFIG_FILE "..\\config\\world\\TurbSim\\TurbSimConfigTemplate.inp"
#define FAST_TEMPLATE_CONFIG_FILE "..\\config\\world\\FAST\\configFileTemplate.fst"
#define FAST_CONFIG_FILE "fast-config.fst"
#define PORTAL_CONFIG_FILE "FASTDimensionalPortalDLL.xml"
#define DIMENSIONAL_PORTAL_PIPE_NAME "FASTDimensionalPortal"

#define TRAINING_WIND_BASE_FILE_NAME "training-wind-file-"
#define EVALUATION_WIND_BASE_FILE_NAME "eval-wind-file-"

CFASTWindTurbine::CFASTWindTurbine(CConfigNode* pConfigNode)
{
	//This class is used both in the DimensionalPortalDLL (pConfigNode will be nullptr) and in RLSimion (pConfigNode will not be nullptr)
	METADATA("World", "FAST-Wind-turbine");
	
	if (pConfigNode)
	{
		m_trainingMeanWindSpeeds = MULTI_VALUE_SIMPLE_PARAM<DOUBLE_PARAM, double>(pConfigNode, "Training-Mean-Wind-Speeds", "Mean wind speeds used in training episodes", 12.5);
		m_evaluationMeanWindSpeeds = MULTI_VALUE_SIMPLE_PARAM<DOUBLE_PARAM, double>(pConfigNode, "Evaluation-Mean-Wind-Speeds", "Mean wind speeds in evaluation episodes", 12.5);
	}

	//model constants
	addConstant("RatedPower", 5e6);				//W
	addConstant("HubHeight", 90);				//m
	addConstant("CutInWindSpeed", 3.0);			//m/s
	addConstant("RatedWindSpeed", 11.4);		//m/s
	addConstant("CutOutWindSpeed", 25.0);		//m/s
	addConstant("CutInRotorSpeed", 0.72256);	//6.9 rpm
	addConstant("CutOutRotorSpeed", 1.26711);	//12.1 rpm
	addConstant("RatedRotorSpeed", 1.26711);	//12.1 rpm
	addConstant("RatedTipSpeed", 8.377);		//80 rpm
	addConstant("RatedGeneratorSpeed", 122.91); //1173.7 rpm
	addConstant("RatedGeneratorTorque", 43093.55);
	addConstant("GearBoxRatio", 97.0);
	addConstant("ElectricalGeneratorEfficiency", 0.944); //%94.4
	addConstant("GeneratorInertia", 534116.0);			//kg*m^2
	addConstant("HubInertia", 115926.0);				//kg*m^2
	addConstant("DriveTrainTorsionalDamping", 6210000.0); //N*m/(rad/s)

	addStateVariable("T_a", "N/m", 0.0, 400000.0);
	addStateVariable("P_a", "W", 0.0, 1600000.0);
	addStateVariable("P_s", "W", 0.0, 6e6);
	addStateVariable("P_e", "W", 0.0, 6e6);
	addStateVariable("E_p", "W", -5e6,5e6);
	addStateVariable("v", "m/s", 1.0, 50.0);
	addStateVariable("omega_r", "rad/s", 0.0, 6.0);
	addStateVariable("d_omega_r", "rad/s^2", -2.0, 2.0);
	addStateVariable("E_omega_r", "rad/s", -4.0, 4.0);
	addStateVariable("omega_g", "rad/s", 0.0, 200.0);
	addStateVariable("d_omega_g", "rad/s^2", -2.0, 2.0);
	addStateVariable("E_omega_g", "rad/s", -4.0, 4.0);
	addStateVariable("beta", "rad", 0.0, 1.570796);
	addStateVariable("d_beta", "rad/s", -0.1396263, 0.1396263);
	addStateVariable("T_g", "N/m", 0.0, 47402.91);
	addStateVariable("d_T_g", "N/m/s", -15000, 15000);
	addStateVariable("E_int_omega_r", "rad", -100.0, 100.0);
	addStateVariable("SimTime", "s", 0.0, 10000.0); //added to avoid precision issues between FAST and RLSimion
	//action handlers
	addActionVariable("beta", "rad", 0.0, 1.570796);
	addActionVariable("T_g", "N/m", 0.0, 47402.91);

	m_pRewardFunction->addRewardComponent(new CToleranceRegionReward("E_p", 100, 1.0));
	m_pRewardFunction->initialize();

	if (CSimionApp::get())
	{
		//input/output files
		CSimionApp::get()->pSimGod->registerInputFile("..\\bin\\FASTDimensionalPortalDLL.dll");
		CSimionApp::get()->pSimGod->registerInputFile("..\\bin\\FAST_win32.exe");
		CSimionApp::get()->pSimGod->registerInputFile("..\\bin\\MAP_win32.dll");
		CSimionApp::get()->pSimGod->registerInputFile("..\\bin\\TurbSim.exe");

		//FAST data files
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\configFileTemplate.fst");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\Cylinder1.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\Cylinder2.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\DU21_A17.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\DU25_A17.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\DU30_A17.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\DU35_A17.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\DU40_A17.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NACA64_A17.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NRELOffshrBsline5MW_AeroDyn_blade.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NRELOffshrBsline5MW_BeamDyn.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NRELOffshrBsline5MW_BeamDyn_Blade.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NRELOffshrBsline5MW_Blade.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NRELOffshrBsline5MW_InflowWindTemplate.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NRELOffshrBsline5MW_Onshore_AeroDyn15.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NRELOffshrBsline5MW_Onshore_ElastoDyn.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NRELOffshrBsline5MW_Onshore_ElastoDyn_BDoutputs.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NRELOffshrBsline5MW_Onshore_ElastoDyn_Tower.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\FAST\\NRELOffshrBsline5MW_Onshore_ServoDyn.dat");
		
		//TurbSim data files
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\TurbSimConfigTemplate.inp");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event00000.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event01917.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event02515.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event02800.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event02893.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event03167.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event03219.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event03326.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event03384.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event03613.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event03692.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event03868.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event04110.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event04434.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event04702.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Event99999.dat");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Events.les");
		CSimionApp::get()->pSimGod->registerInputFile("..\\config\\world\\TurbSim\\EventData\\Events.xtm");
	}
}


void CFASTWindTurbine::deferredLoadStep()
{
	std::string outConfigFileName, exeFileName;
	std::string commandLine;

	//Generate templated TurbSim wind profiles
	bool bLoaded = m_TurbSimConfigTemplate.load(TURBSIM_TEMPLATE_CONFIG_FILE);
	if (bLoaded)
	{
		CLogger::logMessage(MessageType::Info, "Generating TurbSim wind files");

		exeFileName = std::string("..\\bin\\TurbSim.exe");

		//evaluation wind files
		for (unsigned int i = 0; i < m_evaluationMeanWindSpeeds.size(); i++)
		{
			outConfigFileName = std::string(CSimionApp::get()->getOutputDirectory()) + std::string("\\")
				+ std::string(EVALUATION_WIND_BASE_FILE_NAME) + std::to_string(i) + std::string(".inp");
			m_TurbSimConfigTemplate.instantiateConfigFile(outConfigFileName.c_str()
				, CSimionApp::get()->pExperiment->getEpisodeLength() + 30.0	//AnalysisTime
				, CSimionApp::get()->pExperiment->getEpisodeLength() + 30.0	//UsableTime
				, m_evaluationMeanWindSpeeds[i]->get());						//URef

			commandLine = exeFileName + std::string(" ") + outConfigFileName;
			TurbSimProcess.spawn((char*)(commandLine).c_str(), true);
		}
		//set the number of episodes per evaluation
		CSimionApp::get()->pExperiment->setNumEpisodesPerEvaluation((int)m_evaluationMeanWindSpeeds.size());

		//training wind files
		for (unsigned int i = 0; i < m_trainingMeanWindSpeeds.size(); i++)
		{
			outConfigFileName = std::string(CSimionApp::get()->getOutputDirectory()) + std::string("\\") 
				+ std::string(TRAINING_WIND_BASE_FILE_NAME) + std::to_string(i) + std::string(".inp");
			m_TurbSimConfigTemplate.instantiateConfigFile(outConfigFileName.c_str()
				, CSimionApp::get()->pExperiment->getEpisodeLength()+30.0	//AnalysisTime
				, CSimionApp::get()->pExperiment->getEpisodeLength()+30.0	//UsableTime
				, m_trainingMeanWindSpeeds[i]->get());						//URef

			commandLine = exeFileName + std::string(" ") + outConfigFileName;
			TurbSimProcess.spawn((char*)(commandLine).c_str(), true);
		}
	}
	//Load the template used to tell FAST which wind file to use
	m_FASTWindConfigTemplate.load(FAST_WIND_CONFIG_TEMPLATE_FILE);
	
	//copy input files to experiment directory to avoid problems with FAST adding base config file's directory

	commandLine= std::string("copy ..\\config\\world\\FAST\\*.dat ") + std::string(CSimionApp::get()->getOutputDirectory());
	std::replace(commandLine.begin(), commandLine.end(), '/', '\\');
	system(commandLine.c_str());

	CLogger::logMessage(MessageType::Info, "Input files copied");
}

CFASTWindTurbine::~CFASTWindTurbine()
{
	m_namedPipeServer.closeServer();
	CLogger::logMessage(MessageType::Info, "Closed connection to FASTDimensionalPortalDLL");
}



void CFASTWindTurbine::reset(CState *s)
{
	std::string outConfigFileName,windFile;
	FILE *pOutConfigFile;

	//Check if a previous instance of FAST is running and, if it is, kill the process
	//This may happen for slight inaccuracies of DT
	if (FASTprocess.isRunning())
		FASTprocess.kill();
	//If the named pipe is already open, close it
	m_namedPipeServer.closeServer();

	//Open the named pipe server
	//FASTDimensionalPortalDLL.xml -> used to pass the pipe's name to the dll
	bool pipeServerOpened = m_namedPipeServer.openUniqueNamedPipeServer(DIMENSIONAL_PORTAL_PIPE_NAME);
	if (pipeServerOpened)
	{
		outConfigFileName = std::string(CSimionApp::get()->getOutputDirectory()) + std::string("\\")
			+ std::string(PORTAL_CONFIG_FILE);
		fopen_s(&pOutConfigFile, outConfigFileName.c_str(), "w");
		if (pOutConfigFile)
		{
			fprintf_s(pOutConfigFile, "<?xml version=\"1.0\"?>\n<FAST-DIMENSIONAL-PORTAL>\n  <PIPE-NAME>%s</PIPE-NAME>\n</FAST-DIMENSIONAL-PORTAL>"
				, m_namedPipeServer.getPipeFullName());
			fclose(pOutConfigFile);
			CLogger::logMessage(MessageType::Info, "FASTDimensionalPortalDLL: pipe server created");
		}
		else CLogger::logMessage(MessageType::Error, (std::string("Couldn't create config file: ") + outConfigFileName).c_str());
	}
	else CLogger::logMessage(MessageType::Error, "Couldn't open named pipe server");


	//Instantiate the templated FAST config file
	bool bLoaded = m_FASTConfigTemplate.load(FAST_TEMPLATE_CONFIG_FILE);
	if (bLoaded)
	{
		//choose the wind file
		int index;
		if (CSimionApp::get()->pExperiment->isEvaluationEpisode())
		{
			//evaluation wind file
			index = CSimionApp::get()->pExperiment->getEpisodeInEvaluationIndex()-1; //names are 0-based and indices are 1-based
			windFile = std::string(EVALUATION_WIND_BASE_FILE_NAME)
				+ std::to_string(index) + std::string(".bts");
		}
		else
		{
			//training wind file
			index = rand() % m_trainingMeanWindSpeeds.size();
			windFile = std::string(TRAINING_WIND_BASE_FILE_NAME)
				+ std::to_string(m_trainingMeanWindSpeeds[index]->get()) + std::string(".bts");
		}
		outConfigFileName = std::string(CSimionApp::get()->getOutputDirectory()) + std::string("\\")
			+ std::string(FAST_WIND_CONFIG_FILE);
		m_FASTWindConfigTemplate.instantiateConfigFile(outConfigFileName.c_str(), windFile.c_str());

		CLogger::logMessage(MessageType::Info, "Instantiating FAST config file");
		outConfigFileName = std::string(CSimionApp::get()->getOutputDirectory()) + std::string("\\")
			+ std::string(FAST_CONFIG_FILE);
		m_FASTConfigTemplate.instantiateConfigFile(outConfigFileName.c_str()
			, CSimionApp::get()->pExperiment->getEpisodeLength()
			, CSimionApp::get()->pWorld->getDT()
			, FAST_WIND_CONFIG_FILE
		);
	}
	else
		CLogger::logMessage(MessageType::Error, "Couldn't instantiate config file: ../config/world/FAST/configFileTemplate.fst");


	//spawn the FAST exe file
	std::string commandLine;

	commandLine= std::string("..\\bin\\fast_win32.exe");

	commandLine+= std::string(" ") + std::string(CSimionApp::get()->getOutputDirectory())+ std::string("\\")
		+ std::string(FAST_CONFIG_FILE);
	bool bSpawned= FASTprocess.spawn((char*)(commandLine).c_str());
	//wait for the client (FASTDimensionalPortalDLL) to connect
	if (bSpawned && FASTprocess.isRunning())
	{
		CLogger::logMessage(MessageType::Info, "Waiting for the client to connect");
		m_namedPipeServer.waitForClientConnection();
		CLogger::logMessage(MessageType::Info, "Client connected");
		//receive(s)
		m_namedPipeServer.readToBuffer(s->getValueVector(), s->getNumVars() * sizeof(double));
	}
	else
	{
		CLogger::logMessage(MessageType::Info, "FAST process ended prematurely");
		CSimionApp::get()->pExperiment->setTerminalState();
		return;
	}
}

void CFASTWindTurbine::executeAction(CState *s,const CAction *a,double dt)
{
	//Check FAST is still running
	if (!FASTprocess.isRunning())
	{
		CLogger::logMessage(MessageType::Info, "FAST process ended prematurely");
		CSimionApp::get()->pExperiment->setTerminalState();
		return;
	}

	//send(a)
	//here we have to cheat the compiler (const). We don't want to, but we have to
	double* pActionValues = ((CAction*)a)->getValueVector();
	int numBytesToWrite = a->getNumVars() * sizeof(double);
	int numBytesWritten= m_namedPipeServer.writeBuffer(pActionValues, numBytesToWrite);
	if (numBytesToWrite != numBytesWritten)
	{
		CLogger::logMessage(MessageType::Info, "FAST process ended prematurely");
		CSimionApp::get()->pExperiment->setTerminalState();
		return;
	}

	//receive(s')
	int numBytesToRead = s->getNumVars() * sizeof(double);
	int numBytesRead= m_namedPipeServer.readToBuffer(s->getValueVector(), numBytesToRead);
	if (numBytesToRead!=numBytesRead)
	{
		CLogger::logMessage(MessageType::Info, "FAST process ended prematurely");
		CSimionApp::get()->pExperiment->setTerminalState();
		return;
	}
}