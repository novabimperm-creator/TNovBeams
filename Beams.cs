using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TNovCommon;
using Outline = Autodesk.Revit.DB.Outline;
using View = Autodesk.Revit.DB.View;

namespace TNovBeams
{
    
    [Transaction(TransactionMode.Manual)]
    public class Beams : IExternalCommand
    {
        //Список используемых параметров

        static Guid adskMarkIzdParamGuid = new Guid("92ae0425-031b-40a9-8904-023f7389963b"); //A_Марка изделия
        static Guid adskMainPartIzdParamGuid = new Guid("7b011a82-6ead-45ee-a188-7a7721dfb452"); //A_Главная деталь изделия

        private TNovProgressBar beamscutProgressBar;
        private void ThreadStartingPoint()
        {
            this.beamscutProgressBar = new TNovProgressBar();
            this.beamscutProgressBar.Show();
            Dispatcher.Run();
        }
        private XYZ VectorFromHorizVertAngles(double angleHorizD, double angleVertD)
        {
            // Convert degreess to radians.

            double degToRadian = Math.PI * 2 / 360;
            double angleHorizR = angleHorizD * degToRadian;
            double angleVertR = angleVertD * degToRadian;

            // Return unit vector in 3D

            double a = Math.Cos(angleVertR);
            double b = Math.Cos(angleHorizR);
            double c = Math.Sin(angleHorizR);
            double d = Math.Sin(angleVertR);

            return new XYZ(a * b, a * c, d);
        }
        bool ElementNameEndsWithJpg(Element e)
        {
            string s = e.Name;

            return 3 < s.Length && s.EndsWith(".jpg");
        }
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Перемычки";
            //подключение приложения и документа
            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }
            UIDocument uidoc = RevitAPI.UiDocument; Document doc = RevitAPI.Document;
            UIApplication uiApp = RevitAPI.UiApplication; Autodesk.Revit.ApplicationServices.Application rvtApp = uiApp.Application;
            string docName = doc.Title.ToString(); docName = docName.Replace(",", " ");
            string userName = rvtApp.Username; userName = userName.Replace(",", "");
            string docNameUserName = "_" + userName; docName = docName.Replace(docNameUserName, "");
            docName = docName.Replace(",", "");
            #endregion

            string imgPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TNovClient/");

            try
            {

                #region БД
                //аутентификация
                IAuthProvider authProvider = TNovProvider.GetAuthProvider();

                UserInfo user = AuthenticationService.Authenticate(authProvider);
                if (user == null)
                    return Result.Cancelled;

                var repo = new PostgresRepository(ConnectionStringProvider.GetConnectionString());

                var userTask = Task.Run(() => repo.GetOrCreateUserAsync(user.Upn, user.DisplayName));
                if (!userTask.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Не удалось подключиться к БД (превышен таймаут).");
                user = userTask.Result; //получаем user из БД

                FunctionLogger.Log(repo, DBCommandName, user.Upn); //запись в usage

                //работа с данными
                var dataService = new DataService(repo);
                #endregion

                #region Настройки логов

                var viewModel0 = new AppVersionViewModel();

                try
                {
                    viewModel0 = Task.Run(() => dataService.LoadUserDataAsync<AppVersionViewModel>(user.UserId, "Настройки программы")).Result;
                }
                catch (Exception) { }

                if (viewModel0.extendedLogs)

                {
                    var qViewModel = new QuestionWindowViewModel();
                    qViewModel.headtxt = "Включены расширенные логи. " +
                        "Плагин будет работать медленнее, но соберет больше данных. " +
                        "Выключить расширенные логи для ускорения работы?";
                    var qwpfview = new QuestionWindow280(qViewModel);
                    qViewModel.CloseRequest += (s, e) => qwpfview.Close();
                    bool? qok = qwpfview.ShowDialog();
                    if (qok != null && qok == true) { Logger.TurnOffExtendedLogs(); } else Logger.Log("Расширенные логи вкл", 2);
                }
                #endregion


                #region Сбор элементов

                Logger.Initialize(DBCommandName, dateTime, TNovVersion);

                //запрещенные символы
                string rSymbols = @"<>:""/\|?*";

                List<Wall> walls = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls)   //фильтр по категории Стены
                                                                             .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                             .OfClass(typeof(Wall))         //отсеиваем модели в контексте
                                                                             .Cast<Wall>()                     //элементы категории Стены
                                                                             .ToList();                         //формируем список

                List<FamilyInstance> beams = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming)   //фильтр по категории Каркас несущий
                                                                             .WhereElementIsNotElementType()
                                                                             .Cast<FamilyInstance>()
                                                                             .ToList();

                List<ViewDrafting> viewDraftings = new FilteredElementCollector(doc).OfClass(typeof(ViewDrafting))
                    .WhereElementIsNotElementType()
                    .Cast<ViewDrafting>()
                    .ToList();

                List<FamilyInstance> rebar2 = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rebar)   //Несущая арматура семействами
                                                                             .WhereElementIsNotElementType()
                                                                             .OfClass(typeof(FamilyInstance))
                                                                             .Cast<FamilyInstance>()
                                                                             .ToList();

                List<FamilyInstance> concreteBeams = new List<FamilyInstance>();
                List<FamilyInstance> mainBeams = new List<FamilyInstance>();

                if (beams.Count == 0)
                {
                    new InfoWindow280("В проекте отсутствуют перемычки.").ShowDialog();
                    Logger.Log("В проекте отсутствуют перемычки. Завершение работы.", 3);
                    return Result.Failed;
                }

                //проверка наличия параметров
                string parExistErrorMessage = "";
                Element el0 = doc.GetElement(beams.First().Id);
                bool beamPar1exist = Param.ParamExistByGuid(adskMarkIzdParamGuid, el0);
                if (!beamPar1exist) parExistErrorMessage += "Для категории Каркас несущий не добавлен параметр A_Марка изделия. ";
                bool beamPar2exist = Param.ParamExistByGuid(adskMainPartIzdParamGuid, el0);
                if (!beamPar2exist) parExistErrorMessage += "Для категории Каркас несущий не добавлен параметр A_Главная деталь изделия. ";
                Element el1 = doc.GetElement(rebar2.First().Id);
                bool beamPar3exist = Param.ParamExist("Перемычка.Эскиз", el1);
                if (!beamPar3exist) parExistErrorMessage += "Для категории Несущая арматура не добавлен параметр Перемычка.Эскиз. ";

                if (parExistErrorMessage.Length > 0)
                {
                    Logger.Log(parExistErrorMessage, 3);
                    new InfoWindow280(parExistErrorMessage).ShowDialog();
                    return Result.Failed;
                }

                Logger.Log("ищем перемычки брус", 2);

                foreach (var beam in beams) //ищем перемычки брус
                {
                    //Logger.Log("id "+beam.Id.ToString());
                    Element elem = doc.GetElement(beam.Id); bool paramMrkExist = Param.ParamExistByGuid(adskMarkIzdParamGuid, elem);
                    if (paramMrkExist)
                    {
                        string beamMarkValue = beam.get_Parameter(adskMarkIzdParamGuid).AsString();
                        if (beamMarkValue != null)
                        {
                            if (beamMarkValue.Contains("ПР"))
                            {
                                concreteBeams.Add(beam);
                                bool paramMainExist = Param.ParamExistByGuid(adskMainPartIzdParamGuid, elem);
                                if (paramMainExist)
                                {
                                    int beamMainValue = beam.get_Parameter(adskMainPartIzdParamGuid).AsInteger();
                                    if (beamMainValue == 1) mainBeams.Add(beam);
                                }
                            }
                        }
                    }

                }
                Logger.Log("ищем перемычки мет", 2);
                foreach (var rebar in rebar2) //ищем перемычки мет
                {
                    //Logger.Log("id " + rebar.Id.ToString());
                    Element elem = doc.GetElement(rebar.Id); bool paramMrkExist = Param.ParamExistByGuid(adskMarkIzdParamGuid, elem);
                    if (paramMrkExist)
                    {
                        string beamMarkValue = rebar.get_Parameter(adskMarkIzdParamGuid).AsString();
                        if (beamMarkValue != null)
                        {
                            if (beamMarkValue.Contains("ПР"))
                            {
                                bool paramMainExist = Param.ParamExistByGuid(adskMainPartIzdParamGuid, elem);
                                if (paramMainExist)
                                {
                                    int beamMainValue = rebar.get_Parameter(adskMainPartIzdParamGuid).AsInteger();
                                    if (beamMainValue == 1) mainBeams.Add(rebar);
                                }
                            }
                        }
                    }

                }

                int bc = concreteBeams.Count + mainBeams.Count;
                if (bc == 0)
                {
                    var info1 = new InfoWindow280("В проекте отсутствуют перемычки."); info1.ShowDialog();
                    return Result.Failed;
                }
                #endregion

                #region Диалог
                Logger.Log("Диалоговое окно", 1);
                //Вьюмодель
                var viewModel = new BeamsViewModel();
                //Десериализация из БД
                string json = "";
                try
                {
                    json = AsyncHelper.RunSync(() => dataService.LoadModelDataAsync(docName, DBCommandName));
                    if (json.Length < 3) 
                    { 
                        //резервная десериализация с диска
                        bool forProject = true;
                        json js = new json(in DBCommandName, in forProject, out bool canserialize, out string jsonpath);
                        if (canserialize)
                        {
                            viewModel = JsonConvert.DeserializeObject<BeamsViewModel>(File.ReadAllText(jsonpath));
                            Logger.Log("Отсутствует запись в БД. Десериализация с диска прошла успешно", 1);
                        }
                    }
                    else
                    {
                        viewModel = JsonConvert.DeserializeObject<BeamsViewModel>(json);
                        Logger.Log("Десериализация из БД прошла успешно", 1);
                    }
                   
                }
                catch (Exception ex)
                {
                    Logger.Log("Ошибка десериализации: " + ex.Message, 4);
                }
                //Вид
                var wpfview = new BeamsWPF(viewModel);
                viewModel.CloseRequest += (s, e) => wpfview.Close();
                bool? ok = wpfview.ShowDialog();
                if (ok != null && ok == true) { } else { return Result.Cancelled; }
                //Сериализация в БД
                json = JsonConvert.SerializeObject(viewModel);
                try
                {
                    AsyncHelper.RunSync(() => dataService.SaveModelDataAsync(docName, DBCommandName, json));
                    Logger.Log("Сериализация в БД прошла успешно", 1);
                }
                catch (Exception ex)
                {
                    Logger.Log("Ошибка сериализации в БД: " + ex.Message, 4);
                    //резервная сериализация на диск
                    try
                    {
                        bool forProject = true;
                        json js = new json(in DBCommandName, in forProject, out bool canserialize, out string jsonpath);
                        File.WriteAllText(jsonpath, json);
                        Logger.Log("Сериализация на диск прошла успешно", 1);
                    }
                    catch (Exception e) { Logger.Log("Ошибка при сериализации на диск: " + e.Message, 4); }
                }
                #endregion

                #region Рабочий вид
                ElementId workviewid = uidoc.ActiveView.Id;
                if (viewModel.visible != true)
                {
                    //Создаем 3д-вид, где видны все элементы
                    Logger.Log("Настраиваем вид TNov", 1);

                    List<View> views = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Views)   //фильтр по категории Виды
                                                                                 .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                                 .Cast<View>()                     //элементы категории Виды
                                                                                 .ToList();                         //формируем список

                    ViewFamilyType viewFamilyType3D = new FilteredElementCollector(doc)
                                                                                    .OfClass(typeof(ViewFamilyType))
                                                                                    .Cast<ViewFamilyType>()
                                                                                    .FirstOrDefault<ViewFamilyType>(
                                                                                    x => ViewFamily.ThreeDimensional == x.ViewFamily);


                    double angleHorizD = 90;
                    double angleVertD = 0;

                    bool viewexist = false;
                    foreach (View view in views) { if (view.Name == "TNov") { viewexist = true; } }

                    XYZ eye = XYZ.Zero;

                    XYZ forward = VectorFromHorizVertAngles(
                      angleHorizD, angleVertD);

                    XYZ up = VectorFromHorizVertAngles(
                      angleHorizD, angleVertD + 90);

                    ViewOrientation3D viewOrientation3D
                      = new ViewOrientation3D(eye, up, forward);


                    if (viewexist == false)
                    {
                        using (Transaction transaction0 = new Transaction(doc))
                        {

                            transaction0.Start("TNov - рабочий 3D-вид");

                            View3D view3d = View3D.CreateIsometric(doc, viewFamilyType3D.Id);

                            view3d.SetOrientation(viewOrientation3D);

                            view3d.Name = "TNov";

                            workviewid = view3d.Id;

                            transaction0.Commit();
                        }
                    }
                    else
                    {
                        //3d-вид создан либо существует, сбрасываем его подрезку
                        List<View> views1 = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Views)   //фильтр по категории Виды
                                                                                     .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                                     .Cast<View>()                     //элементы категории Виды
                                                                                     .ToList();                         //формируем список
                        foreach (View view in views1) { if (view.Name == "TNov") { /*uidoc.ActiveView = view*/; workviewid = view.Id; } }
                        Autodesk.Revit.DB.View3D workview3d;
                        workview3d = (View3D)doc.GetElement(workviewid);

                        using (Transaction transaction0 = new Transaction(doc))
                        {

                            transaction0.Start("TNov - рабочий 3D-вид");

                            workview3d.IsSectionBoxActive = false;

                            transaction0.Commit();
                        }
                    }
                    Logger.Log("Вид TNov настроен для работы", 1);
                }
                #endregion
                //текущая выборка
                Autodesk.Revit.UI.Selection.Selection selection = commandData.Application.ActiveUIDocument.Selection;

                #region Вырезание
                if (viewModel.cut)
                {
                    //список перемычек на активном виде (если включена галочка только видимые)
                    List<FamilyInstance> concreteBeamsFinalList = new List<FamilyInstance>();
                    if (viewModel.visible == true)
                    {
                        FilteredElementCollector collector = new FilteredElementCollector(doc, doc.ActiveView.Id);
                        foreach (FamilyInstance familyInstance in concreteBeams)
                        {
                            if (collector.ToElementIds().Contains(familyInstance.Id)) concreteBeamsFinalList.Add(familyInstance);
                        }
                    }
                    else if (viewModel.selected)
                    {
                        Logger.Log("Анализ текущей выборки", 1);
                        if (selection == null || selection.GetElementIds() == null)
                        {
                            new InfoWindow280("Для запуска с опцией Выбранные необходимо предварительно выбрать элементы перемычек").ShowDialog();
                            Logger.Log("Элементы не были выбраны. Завершение работы.", 3);
                            return Result.Cancelled;
                        }
                        concreteBeamsFinalList = GetBeamsFromCurrentSelection(doc, selection); //получаем элементы из текущей выборки
                        if (concreteBeamsFinalList == null || concreteBeamsFinalList.Count == 0)
                        {
                            new InfoWindow280("Для запуска с опцией Выбранные необходимо предварительно выбрать элементы перемычек").ShowDialog();
                            Logger.Log("Элементы не были выбраны. Завершение работы.", 3);
                            return Result.Cancelled;
                        }
                    }
                    else
                    {
                        foreach (FamilyInstance familyInstance in concreteBeams) concreteBeamsFinalList.Add(familyInstance);
                    }

                    int allcount = concreteBeamsFinalList.Count;


                    Thread thread = new Thread(new ThreadStart(this.ThreadStartingPoint));
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.IsBackground = true;
                    thread.Start();
                    Thread.Sleep(100);

                    int PBCount = 0;
                    this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.beamscutProgressBar.TNov_ProgressBar.Minimum = (double)PBCount));
                    this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.beamscutProgressBar.value.Text = PBCount.ToString()));
                    this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.beamscutProgressBar.TNov_ProgressBar.Maximum = (double)allcount));
                    this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.beamscutProgressBar.maxvalue.Text = allcount.ToString()));


                    using (Transaction transaction = new Transaction(doc))
                    {
                        try
                        {
                            transaction.Start("TNov - вырез перемычек");
                            Logger.Log("Открываем транзакцию 1 (вырезание)", 1);

                            foreach (FamilyInstance beam in concreteBeamsFinalList)
                            {
                                PBCount++;
                                this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.beamscutProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                                this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.beamscutProgressBar.value.Text = PBCount.ToString()));


                                Element elem1 = doc.GetElement(beam.Id);


                                BoundingBoxXYZ elem1box = elem1.get_BoundingBox(doc.ActiveView);
                                Outline outline1 = new Outline(elem1box.Min, elem1box.Max);
                                BoundingBoxIntersectsFilter bbfilter = new BoundingBoxIntersectsFilter(outline1);
                                FilteredElementCollector collector = new FilteredElementCollector(doc, workviewid);
                                ICollection<ElementId> idsExclude = new List<ElementId> { elem1.Id };
                                collector.Excluding(idsExclude)
                                        .WherePasses(bbfilter);
                                Logger.Log("Перемычка " + beam.Id, 2);
                                List<string> els = new List<string>();
                                foreach (var elem in collector)
                                {
                                    try
                                    {
                                        bool areJoined = JoinGeometryUtils.AreElementsJoined(doc, elem1, elem);
                                        if (!areJoined)
                                        {
                                            JoinGeometryUtils.JoinGeometry(doc, elem1, elem);
                                            Logger.Log("   Элемент " + elem.Id + ": вырезано успешно", 2);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        if (ex.Message.Contains("cannot")) els.Add(elem.Id.IntegerValue.ToString());
                                        else Logger.Log("Перемычка " + beam.Id + " Элемент " + elem.Id + " Ошибка: " + ex.Message, 4);
                                    }

                                }
                                if (els.Count > 0) Logger.Log("   ошибка cannot be joined: " + String.Join(", ", els), 2);
                            }
                            //var info1 = new InfoWindow280("Успешно!"); info1.ShowDialog();
                            transaction.Commit();
                            Logger.Log("Закрываем транзакцию 1", 1);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log("Ошибка: " + ex.Message, 4);
                        }
                        finally
                        {
                            CloseProgressBarSafely();
                        }

                    }
                }
                #endregion
                List<string> badNames = new List<string>();
                #region Параметры
                if (viewModel.pars)
                {


                    using (Transaction transaction2 = new Transaction(doc))
                    {
                        try
                        {
                            transaction2.Start("TNov - сформировать эскизы");
                            Logger.Log("Открываем транзакцию 2 (эскизы)", 1);

                            foreach (var dView in viewDraftings)
                            {
                                bool viewCatParamExist = Param.ParamExist("Орг.КатегорияВида", dView);
                                if (!viewCatParamExist) continue;
                                else
                                {
                                    if (dView.LookupParameter("Орг.КатегорияВида").HasValue)
                                    {
                                        bool isViewRD = dView.LookupParameter("Орг.КатегорияВида").AsString().Contains("Стадия Р");
                                        if (!isViewRD) continue;
                                    }
                                    else continue;
                                }

                                if (dView.Name.StartsWith("ПР"))
                                {
                                    //проверка имени вида
                                    bool badName = false;
                                    foreach (char c in rSymbols)
                                    {
                                        if (dView.Name.Contains(c))
                                        {
                                            badNames.Add(dView.Name);
                                            Logger.Log("Плохое имя вида: " + dView.Name, 1);
                                            badName = true;
                                            break;
                                        }
                                    }
                                    if (badName) continue;

                                    //экспортируем изображение в файл
                                    Logger.Log("Экспортируем изображение " + dView.Name + " в файл", 2);
                                    IList<ElementId> ImageExportList = new List<ElementId>();

                                    ImageExportList.Add(dView.Id);

                                    var BilledeExportOptions = new ImageExportOptions
                                    {
                                        ZoomType = ZoomFitType.FitToPage,
                                        PixelSize = 1024,
                                        FilePath = imgPath + dView.Name,
                                        FitDirection = FitDirectionType.Horizontal,
                                        HLRandWFViewsFileType = ImageFileType.JPEGLossless,
                                        ImageResolution = ImageResolution.DPI_600,
                                        ExportRange = ExportRange.SetOfViews,
                                    };

                                    BilledeExportOptions.SetViewsAndSheets(ImageExportList);

                                    doc.ExportImage(BilledeExportOptions);
                                    string imgPath2 = imgPath + dView.Name + " - Чертежный вид - " + dView.Name + ".jpg";

                                    //ищем существующее изображение в проекте и удаляем его
                                    string searchName = dView.Name + " - Чертежный вид - ";
                                    ICollection<ElementId> imagesToDelete = new List<ElementId>();
                                    FilteredElementCollector col = new FilteredElementCollector(doc).WhereElementIsElementType();
                                    foreach (Element e in col)
                                    {
                                        if (ElementNameEndsWithJpg(e))
                                        {
                                            if (e.Name.Contains(searchName))
                                            {

                                                Logger.Log("Удаляем существующее изображение", 2);
                                                imagesToDelete.Add(e.Id);
                                                doc.Delete(imagesToDelete);
                                                break;
                                            }

                                        }
                                    }

                                    //импортируем новое изображение
                                    Logger.Log("Импортируем изображение", 2);
                                    ImageTypeOptions imageTypeOptions = new ImageTypeOptions(imgPath2, false, ImageTypeSource.Import);
                                    imageTypeOptions.Resolution = 300;
                                    ImageType imageType = ImageType.Create(doc, imageTypeOptions);

                                    //удаляем файл
                                    File.Delete(imgPath2);
                                }
                            }

                            transaction2.Commit();
                            Logger.Log("Закрываем транзакцию 2", 1);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log("Ошибка: " + ex.Message, 4);
                        }
                        finally
                        {
                            CloseProgressBarSafely();
                        }
                    }
                    int allcount = mainBeams.Count;

                    Thread thread = new Thread(new ThreadStart(this.ThreadStartingPoint));
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.IsBackground = true;
                    thread.Start();
                    Thread.Sleep(100);

                    int PBCount = 0;
                    this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.beamscutProgressBar.TNov_ProgressBar.Minimum = (double)PBCount));
                    this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.beamscutProgressBar.value.Text = PBCount.ToString()));
                    this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.beamscutProgressBar.TNov_ProgressBar.Maximum = (double)allcount));
                    this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.beamscutProgressBar.maxvalue.Text = allcount.ToString()));


                    using (Transaction transaction3 = new Transaction(doc))
                    {
                        try
                        {
                            transaction3.Start("TNov - назначить эскизы");
                            Logger.Log("Открываем транзакцию 3 (назначение эскизов)", 1);

                            List<ImageType> imageTypes = new FilteredElementCollector(doc).OfClass(typeof(ImageType))
                        .WhereElementIsElementType()
                        .Cast<ImageType>()
                        .ToList();

                            foreach (FamilyInstance beam in mainBeams)
                            {
                                PBCount++;
                                this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.beamscutProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                                this.beamscutProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.beamscutProgressBar.value.Text = PBCount.ToString()));


                                Element elem1 = doc.GetElement(beam.Id);

                                Logger.Log("Перемычка " + beam.Id, 2);

                                string beamMarkValue = beam.get_Parameter(adskMarkIzdParamGuid).AsString();

                                string searchName = beamMarkValue + " - Чертежный вид - ";

                                foreach (ImageType e in imageTypes)
                                {

                                    if (e.Name.Contains(searchName))
                                    {

                                        var parameter = beam.LookupParameter("Перемычка.Эскиз");
                                        try
                                        {
                                            parameter.Set(e.Id);
                                            Logger.Log("   Элемент " + beam.Id + ": изображение обновлено успешно", 2);
                                        }
                                        catch (Exception ex)
                                        {
                                            Logger.Log("   Элемент " + beam.Id + " Ошибка: " + ex.Message, 4);
                                        }
                                        break;
                                    }


                                }


                            }


                            transaction3.Commit();
                            this.beamscutProgressBar.Dispatcher.Invoke((System.Action)(() => this.beamscutProgressBar.Close()));
                            Logger.Log("Закрываем транзакцию 3", 1);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log("Ошибка: " + ex.Message, 4);
                        }
                        finally
                        {
                            CloseProgressBarSafely();
                        }
                    }
                }

                #endregion

                #region Результат

                if (badNames.Count > 0)
                {
                    new InfoWindow280("В проекте есть чертежные виды ПР с недопустимыми символами (" +
                        rSymbols + ") в именах: " + string.Join(", ", badNames) + ". Эти виды не обработаны, переименуйте виды и перезапустите плагин.").ShowDialog();
                }


                Logger.Log("Завершение работы.", 5);

                #endregion

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Критическая ошибка: {ex.Message}";
                return Result.Failed;
            }
        }
        private static List<FamilyInstance> GetBeamsFromCurrentSelection(Autodesk.Revit.DB.Document doc, Autodesk.Revit.UI.Selection.Selection sel)
        {
            ICollection<ElementId> elementIds = sel.GetElementIds();
            List<FamilyInstance> currentSelection = new List<FamilyInstance>();
            foreach (ElementId elementId in (IEnumerable<ElementId>)elementIds)
            {
                Element elem = doc.GetElement(elementId); bool paramMrkExist = Param.ParamExistByGuid(adskMarkIzdParamGuid, elem);
                if (paramMrkExist)
                {
                    string beamMarkValue = elem.get_Parameter(adskMarkIzdParamGuid).AsString();
                    if (beamMarkValue != null)
                    {
                        if (beamMarkValue.Contains("ПР"))
                        {
                            currentSelection.Add(doc.GetElement(elementId) as FamilyInstance);
                        }
                    }
                }

            }
            return currentSelection;
        }
        private void CloseProgressBarSafely()
        {
            if (beamscutProgressBar != null &&
                beamscutProgressBar.Dispatcher != null &&
                !beamscutProgressBar.Dispatcher.HasShutdownStarted)
            {
                beamscutProgressBar.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (beamscutProgressBar.IsLoaded)
                        beamscutProgressBar.Close();
                    // Завершаем цикл сообщений диспетчера, чтобы поток завершился
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }));
            }
        }
        
    }

    
}
