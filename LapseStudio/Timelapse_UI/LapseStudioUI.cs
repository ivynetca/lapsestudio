﻿using System;
using System.Linq;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using MessageTranslation;
using Timelapse_API;
using System.IO.Compression;
using System.Runtime.Serialization.Formatters.Binary;

namespace Timelapse_UI
{
    public class LapseStudioUI
    {
        #region Variables

        public MessageBox MsgBox;
        public FileDialog FDialog;
        private bool ProjectSaved = true;
        private bool IsTableUpdate = false;
        private string ProjectSavePath;
        private IUIHandler UIHandler;
        private object[] DefArray = new object[] { "0", false, "N/A", "0.000", "N/A", "N/A", "N/A" };

        public BrightnessGraph MainGraph;

        #endregion

        public LapseStudioUI(Platform RunningPlatform, IUIHandler UIHandler, MessageBox MsgBox, FileDialog FDialog)
        {
            this.UIHandler = UIHandler;
            this.MsgBox = MsgBox;
            this.FDialog = FDialog;
            Error.Init(MsgBox);

            Init(RunningPlatform);

            AppDomain currentDomain = AppDomain.CurrentDomain;
            currentDomain.UnhandledException += HandleUnhandledException;

            ProjectManager.BrightnessCalculated += CurrentProject_BrightnessCalculated;
            ProjectManager.FramesLoaded += CurrentProject_FramesLoaded;
            ProjectManager.ProgressChanged += CurrentProject_ProgressChanged;
            ProjectManager.WorkDone += CurrentProject_WorkDone;
            MsgBox.InfoTextChanged += MsgBox_InfoTextChanged;
        }

        #region Event handling

        private void HandleUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Error.Report("Unhandled Exception", (Exception)e.ExceptionObject);
        }

        private void CurrentProject_WorkDone(object sender, WorkFinishedEventArgs e)
        {
            try
            {
                if (!e.Cancelled)
                {
                    switch (e.Topic)
                    {
                        case Work.ProcessThumbs:
                            UIHandler.RefreshImages();
                            break;
                        case Work.LoadProject:
                            UIHandler.InitOpenedProject();
                            break;
                    }

                    UIHandler.SetStatusLabel(Message.GetString(e.Topic.ToString()) + " " + Message.GetString("is done"));
                }
                else { UIHandler.SetStatusLabel(Message.GetString(e.Topic.ToString()) + " " + Message.GetString("got cancelled")); }
                UIHandler.SetProgress(0);
            }
            catch (Exception ex) { Error.Report("Work finished", ex); }
        }

        private void CurrentProject_ProgressChanged(object sender, ProgressChangeEventArgs e)
        {
            try
            {
                int p = e.ProgressPercentage;
                if (e.Topic == ProgressType.LoadThumbnails)
                {
                    UIHandler.SetTableRow(p, GetRow(p), true);
                    p = 100 * p / ProjectManager.CurrentProject.Frames.Count;
                    p = (int)(66.66f + (33.33f * p) / 100f);
                }
                UIHandler.SetProgress(p);
                UIHandler.SetStatusLabel(Message.GetString(e.Topic.ToString()));
            }
            catch (Exception ex) { Error.Report("Progress changed", ex); }
        }

        private void CurrentProject_FramesLoaded(object sender, WorkFinishedEventArgs e)
        {
            try
            {
                UIHandler.SetStatusLabel(Message.GetString("Frames loaded"));
                UIHandler.RefreshImages();
                UIHandler.SetProgress(0);
                UIHandler.InitAfterFrameLoad();
                UIHandler.SelectTableRow(0);
            }
            catch (Exception ex) { Error.Report("Frames loading finished", ex); }
        }

        private void CurrentProject_BrightnessCalculated(object sender, WorkFinishedEventArgs e)
        {
            try
            {
                UIHandler.SetStatusLabel(Message.GetString("Brightness calculated"));
                UIHandler.SetProgress(0);
                UpdateTable(false);
                MainGraph.RefreshGraph();
            }
            catch (Exception ex) { Error.Report("Brightness calculation finished", ex); }
        }

        private void MsgBox_InfoTextChanged(string Value)
        {
            UIHandler.SetStatusLabel(Value);
        }

        #endregion

        #region Shared Methods

        public bool Quit(ClosingReason reason)
        {
            //the return value defines if the quiting should get canceled or not
            if (reason != ClosingReason.Error)
            {
                WindowResponse res;

                if (ProjectManager.CurrentProject.IsWorking)
                {
                    res = MsgBox.ShowMessage(MessageContent.BusyClose);
                    if (res == WindowResponse.No) { return true; }
                    else if (res == WindowResponse.Yes) { ProjectManager.Cancel(); }
                }

                res = AskForSaving();
                if (res == WindowResponse.Cancel) { return true; }
            }
            else { ProjectManager.Cancel(); }

            LSSettings.Save();

            ProjectManager.BrightnessCalculated -= CurrentProject_BrightnessCalculated;
            ProjectManager.FramesLoaded -= CurrentProject_FramesLoaded;
            ProjectManager.ProgressChanged -= CurrentProject_ProgressChanged;
            ProjectManager.WorkDone -= CurrentProject_WorkDone;

            if (ProjectManager.CurrentProject.IsWorking) { ProjectManager.CurrentProject.IsWorkingWaitHandler.WaitOne(10000); }

            UIHandler.ReleaseUIData();
            ProjectManager.Close();

            UIHandler.QuitApplication();
            return false;
        }

        public WindowResponse AskForSaving()
        {
            if (!ProjectSaved)
            {
                WindowResponse res = MsgBox.ShowMessage(MessageContent.SaveQuestion);
                if (res == WindowResponse.Yes) { Click_SaveProject(false); return WindowResponse.Ok; }
                else if (res == WindowResponse.No) { return WindowResponse.Ok; }
                else { return WindowResponse.Cancel; }
            }
            return WindowResponse.Ok;
        }

        public void SetSaveStatus(bool isSaved)
        {
            ProjectSaved = isSaved;
            string t = "LapseStudio";
            if (isSaved && !String.IsNullOrEmpty(ProjectSavePath)) { t += " - " + Path.GetFileNameWithoutExtension(ProjectSavePath); }
            else if (String.IsNullOrEmpty(ProjectSavePath) && isSaved) { t += " - " + Message.GetString("NewProject"); }
            else if (String.IsNullOrEmpty(ProjectSavePath) && !isSaved) { t += " - " + Message.GetString("NewProject") + "*"; }
            else { t += " - " + Path.GetFileNameWithoutExtension(ProjectSavePath) + "*"; }
            UIHandler.SetWindowTitle(t);
        }

        public void SavingProject()
        {
            SavingStorage Storage = new SavingStorage();
            using (GZipStream str = new GZipStream(File.Create(ProjectSavePath), CompressionMode.Compress))
            {
                BinaryFormatter ft = new BinaryFormatter();
                ft.Serialize(str, Storage);
            }
            MsgBox.ShowMessage(MessageContent.ProjectSaved);
        }

        public void OpeningProject()
        {
            using (GZipStream str = new GZipStream(File.Open(ProjectSavePath, FileMode.Open), CompressionMode.Decompress))
            {
                BinaryFormatter ft = new BinaryFormatter();
                SavingStorage Storage = (SavingStorage)ft.Deserialize(str);
                ProjectManager.OpenProject(Storage);
            }
        }

        public void ProcessFiles()
        {
            bool ask = true;
            if (LSSettings.UsedProgram == ProjectType.CameraRaw) { ask = false; }
            else if (LSSettings.UsedProgram == ProjectType.LapseStudio)
            {
                ask = true;
                ((ProjectLS)ProjectManager.CurrentProject).SaveFormat = LSSettings.SaveFormat;
            }
            else if (LSSettings.UsedProgram == ProjectType.RawTherapee)
            {
                if (!File.Exists(LSSettings.RTPath))
                {
                    string NewRTPath = ProjectRT.SearchForRT();
                    if (NewRTPath != null)
                    {
                        LSSettings.RTPath = NewRTPath;
                        LSSettings.Save();
                        ((ProjectRT)ProjectManager.CurrentProject).RTPath = NewRTPath;
                    }
                    else
                    {
                        MsgBox.Show(Message.GetString("RawTherapee can't be found. Abort!"));
                        return;
                    }
                }
                ask = ((ProjectRT)ProjectManager.CurrentProject).RunRT;
                ((ProjectRT)ProjectManager.CurrentProject).SaveFormat = LSSettings.SaveFormat;
            }

            if (ask)
            {
                using (FileDialog fdlg = FDialog.CreateDialog(FileDialogType.SelectFolder, Message.GetString("Select Folder")))
                {
                    if (Directory.Exists(LSSettings.LastProcDir)) fdlg.InitialDirectory = LSSettings.LastProcDir;
                    if (fdlg.Show() == WindowResponse.Ok)
                    {
                        LSSettings.LastProcDir = fdlg.SelectedPath;
                        LSSettings.Save();
                        if (ProjectManager.CurrentProject.IsBrightnessCalculated) { ProjectManager.SetAltBrightness(MainGraph.Points); }
                        ProjectManager.ProcessFiles(fdlg.SelectedPath);
                    }
                }
            }
            else
            {
                if (ProjectManager.CurrentProject.IsBrightnessCalculated) { ProjectManager.SetAltBrightness(MainGraph.Points); }
                ProjectManager.ProcessFiles();
            }
            SetSaveStatus(false);
        }

        public void Init(Platform RunningPlatform)
        {
            ProjectManager.Init(RunningPlatform);
            LSSettings.Init();
            string lang;
            switch (LSSettings.UsedLanguage)
            {
                case Language.English: lang = "en"; break;
                case Language.German: lang = "de"; break;

                default: lang = "en"; break;
            }
            Message.Init(lang);
        }

        public void InitBaseUI()
        {
            ProjectManager.NewProject(LSSettings.UsedProgram);
            if (LSSettings.UsedProgram == ProjectType.RawTherapee)
            {
                ((ProjectRT)ProjectManager.CurrentProject).RunRT = LSSettings.RunRT;
                ((ProjectRT)ProjectManager.CurrentProject).RTPath = LSSettings.RTPath;
                ((ProjectRT)ProjectManager.CurrentProject).KeepPP3 = LSSettings.KeepPP3;
                ((ProjectRT)ProjectManager.CurrentProject).JpgQuality = LSSettings.JpgQuality;
                ((ProjectRT)ProjectManager.CurrentProject).TiffCompression = LSSettings.TiffCompression != TiffCompressionFormat.None;
                ((ProjectRT)ProjectManager.CurrentProject).BitDepth16 = LSSettings.BitDepth == ImageBitDepth.bit16;
            }
            MainGraph.Init();
            ProjectManager.Threadcount = LSSettings.Threadcount;
            UIHandler.InitUI();
            UIHandler.InitTable();
            UIHandler.RefreshImages();
        }

        public bool CheckBusy()
        {
            if (ProjectManager.CurrentProject.IsWorking) { MsgBox.ShowMessage(MessageContent.IsBusy); return true; }
            else return false;
        }

        public void SettingsChanged()
        {
            if (LSSettings.UsedProgram != ProjectManager.CurrentProject.Type) { if (Click_NewProject() == WindowResponse.Cancel) { LSSettings.UsedProgram = ProjectManager.CurrentProject.Type; } }
            else if (LSSettings.UsedProgram == ProjectType.RawTherapee) 
            {
                ((ProjectRT)ProjectManager.CurrentProject).RTPath = LSSettings.RTPath;
                ((ProjectRT)ProjectManager.CurrentProject).RunRT = LSSettings.RunRT;
                ((ProjectRT)ProjectManager.CurrentProject).KeepPP3 = LSSettings.KeepPP3;
            }
            ProjectManager.Threadcount = LSSettings.Threadcount;
        }

        public void UpdateTable(bool fill)
        {
            IsTableUpdate = true;
            for (int i = 0; i < ProjectManager.CurrentProject.Frames.Count; i++)
            {
                UIHandler.SetTableRow(i, GetRow(i), fill);
            }
            IsTableUpdate = false;
        }

        public void OpenMetaData(int index)
        {
            if (LSSettings.UsedProgram == ProjectType.CameraRaw)
            {
                XMP CurXmp = ((FrameACR)ProjectManager.CurrentProject.Frames[index]).XMPFile;
                if (CurXmp == null || (CurXmp != null && CurXmp.Values.Count == 0))
                {
                    WindowResponse res = MsgBox.Show(Message.GetString(@"No XMP associated with this file. Do you want to reload to check if there is one now?
Yes reloads the files XMP values.
No lets you load values from a standalone XMP file."), MessageWindowType.Question, MessageWindowButtons.YesNoCancel);
                    if (res == WindowResponse.Yes) { ProjectManager.ReadXMP(); return; }
                    else if (res == WindowResponse.Cancel) return;

                    using (FileDialog fdlg = FDialog.CreateDialog(FileDialogType.OpenFile, Message.GetString("Open XMP")))
                    {
                        fdlg.AddFileTypeFilter(new FileTypeFilter(Message.GetString("XMP"), "xmp", "XMP"));
                        if (Directory.Exists(LSSettings.LastMetaDir)) fdlg.InitialDirectory = LSSettings.LastMetaDir;

                        if (fdlg.Show() == WindowResponse.Ok)
                        {
                            LSSettings.LastMetaDir = Path.GetDirectoryName(fdlg.SelectedPath);
                            LSSettings.Save();
                            ProjectManager.AddKeyframe(index, fdlg.SelectedPath);
                        }
                    }
                }
                else { ProjectManager.AddKeyframe(index); }
            }
            else if (LSSettings.UsedProgram == ProjectType.RawTherapee)
            {
                using (FileDialog fdlg = FDialog.CreateDialog(FileDialogType.OpenFile, Message.GetString("Open PP3")))
                {
                    fdlg.AddFileTypeFilter(new FileTypeFilter(Message.GetString("Postprocessing Profile"), "PP3", "pp3"));
                    if (Directory.Exists(LSSettings.LastMetaDir)) fdlg.InitialDirectory = LSSettings.LastMetaDir;

                    if (fdlg.Show() == WindowResponse.Ok)
                    {
                        LSSettings.LastMetaDir = Path.GetDirectoryName(fdlg.SelectedPath);
                        LSSettings.Save();
                        ProjectManager.AddKeyframe(index, fdlg.SelectedPath);
                    }
                }
            }
            else { ProjectManager.AddKeyframe(index); }

            if (ProjectManager.CurrentProject.Frames[index].IsKeyframe) MsgBox.ShowMessage(MessageContent.KeyframeAdded);
            else MsgBox.ShowMessage(MessageContent.KeyframeNotAdded);
        }

        public void UpdateBrightness(int Row, string CurrentValue)
        {
            double val;
            try { val = Convert.ToDouble(CurrentValue); }
            catch { return; }

            double change = val - ProjectManager.CurrentProject.Frames[Row].AlternativeBrightness;
            ProjectManager.CurrentProject.Frames[Row].AlternativeBrightness = val;

            for (int i = Row + 1; i < ProjectManager.CurrentProject.Frames.Count; i++)
            {
                ProjectManager.CurrentProject.Frames[i].AlternativeBrightness += change;
            }

            double min = ProjectManager.CurrentProject.Frames.Min(p => p.AlternativeBrightness);
            if (min < 0)
            {
                for (int i = 0; i < ProjectManager.CurrentProject.Frames.Count; i++)
                {
                    ProjectManager.CurrentProject.Frames[i].AlternativeBrightness += min + 5;
                }
                change += min + 5;
            }

            if (!IsTableUpdate) UpdateTable(false);
        }

        private ArrayList GetRow(int row)
        {
            Frame CurFrame = ProjectManager.CurrentProject.Frames[row];
            ArrayList LScontent = new ArrayList(DefArray);
            int index;

            //Nr
            index = (int)TableLocation.Nr;
            LScontent[index] = Convert.ToString(row + 1);
            //Filenames
            index = (int)TableLocation.Filename;
            LScontent[index] = CurFrame.Filename;
            //Brightness
            index = (int)TableLocation.Brightness;
            LScontent[index] = CurFrame.AlternativeBrightness.ToString("N3");
            //AV
            index = (int)TableLocation.AV;
            if (CurFrame.AVstring != null) { LScontent[index] = CurFrame.AVstring; }
            else { LScontent[index] = "N/A"; }
            //TV
            index = (int)TableLocation.TV;
            if (CurFrame.TVstring != null) { LScontent[index] = CurFrame.TVstring; }
            else { LScontent[index] = "N/A"; }
            //ISO
            index = (int)TableLocation.ISO;
            if (CurFrame.SVstring != null) { LScontent[index] = CurFrame.SVstring; }
            else { LScontent[index] = "N/A"; }
            //Keyframes
            index = (int)TableLocation.Keyframe;
            if (CurFrame.IsKeyframe) { LScontent[index] = true; }
            else { LScontent[index] = false; }

            return LScontent;
        }

        #endregion

        #region User Input Methods

        public void Click_SaveProject(bool alwaysAsk)
        {
            if (CheckBusy()) return;
            if (File.Exists(ProjectSavePath) && !alwaysAsk) { SavingProject(); }
            else
            {
                using (FileDialog fdlg = FDialog.CreateDialog(FileDialogType.SaveFile, Message.GetString("Save Project")))
                {
                    fdlg.AddFileTypeFilter(new FileTypeFilter(Message.GetString("LapseStudio Project"), "lasp"));
                    if (Directory.Exists(LSSettings.LastProjDir)) fdlg.InitialDirectory = LSSettings.LastProjDir;
                    if (fdlg.Show() == WindowResponse.Ok)
                    {
                        if (Path.GetExtension(fdlg.SelectedPath) != ".lasp") { Path.ChangeExtension(fdlg.SelectedPath, ".lasp"); }
                        LSSettings.LastProjDir = Path.GetDirectoryName(fdlg.SelectedPath);
                        LSSettings.Save();
                        ProjectSavePath = fdlg.SelectedPath;
                        SavingProject();
                    }
                }
            }
            SetSaveStatus(true);
        }

        public void Click_OpenProject()
        {
            if (CheckBusy()) return;
            using (FileDialog fdlg = FDialog.CreateDialog(FileDialogType.OpenFile, Message.GetString("Open Project")))
            {
                fdlg.AddFileTypeFilter(new FileTypeFilter(Message.GetString("LapseStudio Project"), "lasp"));
                if (Directory.Exists(LSSettings.LastProjDir)) fdlg.InitialDirectory = LSSettings.LastProjDir;
                if (fdlg.Show() == WindowResponse.Ok)
                {
                    LSSettings.LastProjDir = System.IO.Path.GetDirectoryName(fdlg.SelectedPath);
                    LSSettings.Save();
                    ProjectSavePath = fdlg.SelectedPath;
                    OpeningProject();
                }
            }
            SetSaveStatus(true);
        }

        public void Click_AddFrames()
        {
            if (CheckBusy()) return;
            if (ProjectManager.CurrentProject.Frames.Count == 0)
            {
                using (FileDialog fdlg = FDialog.CreateDialog(FileDialogType.SelectFolder, Message.GetString("Select Folder")))
                {
                    if (Directory.Exists(LSSettings.LastImgDir)) fdlg.InitialDirectory = LSSettings.LastImgDir;
                    if (fdlg.Show() == WindowResponse.Ok)
                    {
                        LSSettings.LastImgDir = fdlg.SelectedPath;
                        LSSettings.Save();
                        if (ProjectManager.AddFrames(fdlg.SelectedPath)) SetSaveStatus(false);
                        else { MsgBox.ShowMessage(MessageContent.NotEnoughValidFiles); }
                    }
                }
            }
            else { MsgBox.ShowMessage(MessageContent.FramesAlreadyAdded); }
        }

        public WindowResponse Click_NewProject()
        {
            if (CheckBusy()) return WindowResponse.Cancel;
            WindowResponse res = AskForSaving();
            if (res == WindowResponse.Ok)
            {
                InitBaseUI();
                SetSaveStatus(true);
            }
            return res;
        }

        public void Click_Process()
        {
            if (CheckBusy()) return;
            if (ProjectManager.CurrentProject.KeyframeCount == 0) { MsgBox.ShowMessage(MessageContent.KeyframecountLow); }
            else if (ProjectManager.CurrentProject.IsBrightnessCalculated) { ProcessFiles(); }
            else if (LSSettings.UsedProgram == ProjectType.LapseStudio) { MsgBox.ShowMessage(MessageContent.BrightnessNotCalculatedError); }
            else if (MsgBox.ShowMessage(MessageContent.BrightnessNotCalculatedWarning) == WindowResponse.Yes) { ProcessFiles(); }
        }

        public void Click_RefreshMetadata()
        {
            if (CheckBusy()) return;
            if (ProjectManager.CurrentProject.Type == ProjectType.CameraRaw) { ProjectManager.ReadXMP(); }
        }

        public void Click_Calculate(BrightnessCalcType Type)
        {
            if (CheckBusy()) return;
            if (ProjectManager.CurrentProject.IsBrightnessCalculated && MsgBox.ShowMessage(MessageContent.BrightnessAlreadyCalculated) == WindowResponse.No) return;
            if (ProjectManager.CurrentProject.Frames.Count > 1) { ProjectManager.CalculateBrightness(Type); }
            else { MsgBox.ShowMessage(MessageContent.NotEnoughFrames); }
        }

        public void Click_ThumbEdit()
        {
            if (ProjectManager.CurrentProject.IsBrightnessCalculated)
            {
                ProjectManager.SetAltBrightness(MainGraph.Points);
                ProjectManager.ProcessThumbs();
            }
        }

        public void Click_KeyframeToggle(int Row, bool Toggled)
        {
            if (!Toggled) OpenMetaData(Row);
            else ProjectManager.RemoveKeyframe(Row, false);
            UIHandler.SetTableRow(Row, GetRow(Row), false);
        }

        public void Click_BrightnessSlider(double Value)
        {
            if (ProjectManager.CurrentProject.IsBrightnessCalculated)
            {
                for (int i = 1; i < ProjectManager.CurrentProject.Frames.Count; i++)
                {
                    //LTODO: make brightness scale working
                    double orig1 = ProjectManager.CurrentProject.Frames[i - 1].OriginalBrightness;
                    double orig2 = ProjectManager.CurrentProject.Frames[i].OriginalBrightness;
                    ProjectManager.CurrentProject.Frames[i].AlternativeBrightness = orig2 + ((orig2 - orig1) * Value / 100);
                }
                MainGraph.RefreshGraph();
            }
        }

        public string Click_CalcTypeChanged(BrightnessCalcType Type)
        {
            switch (Type)
            {
                case BrightnessCalcType.Advanced: return GeneralValues.BrCalc_Advanced_Ex;
                case BrightnessCalcType.AdvancedII: return GeneralValues.BrCalc_AdvancedII_Ex;
                case BrightnessCalcType.Exif: return GeneralValues.BrCalc_Exif_Ex;
                case BrightnessCalcType.Lab: return GeneralValues.BrCalc_Lab_Ex;
                case BrightnessCalcType.Simple: return GeneralValues.BrCalc_Simple_Ex;

                default: return "";
            }
        }

        #endregion
    }
}
