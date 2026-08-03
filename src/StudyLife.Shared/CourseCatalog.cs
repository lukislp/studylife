namespace StudyLife.Shared;

public class CourseDto
{
    public int Id { get; set; }
    public int Semester { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Color { get; set; } = "#6C5CE7";
    public string Icon { get; set; } = "📚";
    public List<string> Topics { get; set; } = new();
    /// <summary>ECTS credits of the module. Placeholder values (5 regular, 10 project, 12 bachelor thesis) - adjust to the real program as needed.</summary>
    public int Ects { get; set; } = 5;
    /// <summary>Optional subgroup within a semester (e.g. "Elective modules A"). Rendered as a section heading.</summary>
    public string? Group { get; set; }
}

/// <summary>
/// The study programme's course catalog. Lives here (rather than only in the
/// client) so the server can expose it via GET /api/courses - see
/// docs/ARCHITECTURE.md for why that mattered (course name/icon/color used to
/// only exist client-side, invisible to any other consumer incl. Home Assistant).
/// </summary>
public static class CourseCatalog
{
    /// <summary>
    /// ECTS quota per elective group. At most this many ECTS are credited per group,
    /// regardless of how many modules the student completes.
    /// Together with the mandatory-module ECTS this yields the total program of 180 ECTS.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> GroupEctsQuotas =
        new Dictionary<string, int>
        {
            ["Wahlpflichtmodule A (5 ECTS)"] = 5,
            ["Wahlpflichtmodule B (10 ECTS)"] = 10,
            ["Wahlpflichtmodule C (10 ECTS)"] = 10,
            ["Wahlpflichtmodule D (30 ECTS)"] = 30,
        };

    /// <summary>Total number of creditable ECTS of the study program (180).</summary>
    public static int CalcTotalEcts(IEnumerable<CourseDto> courses)
    {
        var ungrouped = courses.Where(c => c.Group == null).Sum(c => c.Ects);
        var groupQuotas = GroupEctsQuotas.Values.Sum();
        return ungrouped + groupQuotas;
    }

    /// <summary>
    /// Creditable ECTS for a set of completed course IDs.
    /// For elective groups, at most the group quota is credited.
    /// </summary>
    public static int CalcEctsEarned(IEnumerable<CourseDto> courses, IEnumerable<int> completedIds)
    {
        var completed = new HashSet<int>(completedIds);
        var courseList = courses.ToList();

        var ungrouped = courseList
            .Where(c => c.Group == null && completed.Contains(c.Id))
            .Sum(c => c.Ects);

        var grouped = courseList
            .Where(c => c.Group != null && completed.Contains(c.Id))
            .GroupBy(c => c.Group!)
            .Sum(g =>
            {
                var earned = g.Sum(c => c.Ects);
                var quota = GroupEctsQuotas.TryGetValue(g.Key, out var q) ? q : earned;
                return Math.Min(earned, quota);
            });

        return ungrouped + grouped;
    }

    public static List<CourseDto> AppliedAICourses => new()
    {
        // Semester 1
        new() { Id = 1,  Semester = 1, Name = "Artificial Intelligence",                                                    Code = "AI-101",    Color = "#6C5CE7", Icon = "✦",  Ects = 5,  Topics = new() { "KI-Grundlagen", "Suchalgorithmen", "Wissensrepräsentation", "Planung", "KI-Überblick" } },
        new() { Id = 2,  Semester = 1, Name = "Einführung in die Programmierung mit Python",                                Code = "PY-101",    Color = "#00B894", Icon = "🐍",  Ects = 5,  Topics = new() { "Syntax", "Datentypen", "Kontrollstrukturen", "Funktionen", "OOP-Grundlagen" } },
        new() { Id = 3,  Semester = 1, Name = "Mathematik: Analysis",                                                       Code = "MA-101",    Color = "#0984E3", Icon = "∫",   Ects = 5,  Topics = new() { "Grenzwerte", "Ableitungen", "Integrale", "Reihen", "Differentialgleichungen" } },
        new() { Id = 4,  Semester = 1, Name = "Einführung in das wissenschaftliche Arbeiten für IT und Technik",            Code = "WA-101",    Color = "#FDCB6E", Icon = "📄",  Ects = 5,  Topics = new() { "Literaturrecherche", "Zitieren", "Methodologie", "Wissenschaftstheorie", "Schreiben" } },
        new() { Id = 5,  Semester = 1, Name = "Projekt: Objektorientierte und funktionale Programmierung mit Python",       Code = "PY-102P",   Color = "#E17055", Icon = "⚙",   Ects = 5,  Topics = new() { "OOP", "Funktionale Programmierung", "Lambda", "Dekoratoren", "Projektarbeit" } },
        // Semester 2
        new() { Id = 6,  Semester = 2, Name = "Mathematik: Lineare Algebra",                                                Code = "MA-201",    Color = "#6C5CE7", Icon = "∑",   Ects = 5,  Topics = new() { "Vektoren", "Matrizen", "Eigenwerte", "Lineare Gleichungssysteme", "Transformationen" } },
        new() { Id = 7,  Semester = 2, Name = "Statistik - Wahrscheinlichkeit und deskriptive Statistik",                   Code = "ST-201",    Color = "#00B894", Icon = "📊",  Ects = 5,  Topics = new() { "Wahrscheinlichkeit", "Verteilungen", "Mittelwert", "Varianz", "Visualisierung" } },
        new() { Id = 8,  Semester = 2, Name = "Statistik - Induktive Statistik",                                            Code = "ST-202",    Color = "#0984E3", Icon = "📈",  Ects = 5,  Topics = new() { "Hypothesentests", "Konfidenzintervalle", "Regression", "ANOVA", "Schätzung" } },
        new() { Id = 9,  Semester = 2, Name = "Cloud Computing",                                                            Code = "CC-201",    Color = "#FDCB6E", Icon = "☁",   Ects = 5,  Topics = new() { "AWS", "Azure", "GCP", "Serverless", "IaaS / PaaS / SaaS" } },
        new() { Id = 10, Semester = 2, Name = "Projekt: Cloud Programming",                                                 Code = "CC-202P",   Color = "#E17055", Icon = "🚀",  Ects = 5,  Topics = new() { "Cloud-Architektur", "Deployment", "Container", "CI/CD", "Projektarbeit" } },
        // Semester 3
        new() { Id = 11, Semester = 3, Name = "Maschinelles Lernen - Supervised Learning",                                  Code = "ML-301",    Color = "#E84393", Icon = "🤖",  Ects = 5,  Topics = new() { "Regression", "Klassifikation", "SVM", "Decision Trees", "Evaluation" } },
        new() { Id = 12, Semester = 3, Name = "Maschinelles Lernen - Unsupervised Learning und Feature Engineering",        Code = "ML-302",    Color = "#A29BFE", Icon = "🔍",  Ects = 5,  Topics = new() { "Clustering", "PCA", "Dimensionsreduktion", "Feature Engineering", "Anomalieerkennung" } },
        new() { Id = 13, Semester = 3, Name = "Neuronale Netze und Deep Learning",                                          Code = "DL-301",    Color = "#6C5CE7", Icon = "🧠",  Ects = 5,  Topics = new() { "Neuronale Netze", "CNN", "RNN", "Backpropagation", "Frameworks" } },
        new() { Id = 14, Semester = 3, Name = "Einführung in Computer Vision",                                              Code = "CV-301",    Color = "#00CEC9", Icon = "👁",   Ects = 5,  Topics = new() { "Bildverarbeitung", "Objekterkennung", "Segmentierung", "OpenCV", "GANs" } },
        new() { Id = 15, Semester = 3, Name = "Projekt: Computer Vision",                                                   Code = "CV-302P",   Color = "#55EFC4", Icon = "📷",  Ects = 5,  Topics = new() { "Computer-Vision-Projekt", "Datenpipeline", "Modelltraining", "Deployment", "Präsentation" } },
        // Semester 4
        new() { Id = 16, Semester = 4, Name = "Einführung in das Reinforcement Learning",                                   Code = "RL-401",    Color = "#E17055", Icon = "🎮",  Ects = 5,  Topics = new() { "MDP", "Q-Learning", "Policy Gradient", "Actor-Critic", "OpenAI Gym" } },
        new() { Id = 17, Semester = 4, Name = "Einführung in Datenschutz und IT-Sicherheit",                                Code = "DS-401",    Color = "#FDCB6E", Icon = "🔒",  Ects = 5,  Topics = new() { "DSGVO", "IT-Sicherheit", "Kryptographie", "Angriffe", "Datenschutzrecht" } },
        new() { Id = 18, Semester = 4, Name = "Ethische und rechtliche Aspekte in der KI",                                  Code = "ET-401",    Color = "#FD79A8", Icon = "⚖",   Ects = 5,  Topics = new() { "KI-Ethik", "Bias & Fairness", "Regulierung", "Verantwortung", "Erklärbarkeit" } },
        new() { Id = 19, Semester = 4, Name = "Einführung in NLP",                                                          Code = "NLP-401",   Color = "#00B894", Icon = "💬",  Ects = 5,  Topics = new() { "Tokenisierung", "Embeddings", "BERT", "GPT", "Sentiment-Analyse" } },
        new() { Id = 20, Semester = 4, Name = "Projekt: NLP",                                                               Code = "NLP-402P",  Color = "#0984E3", Icon = "✍",   Ects = 5,  Topics = new() { "NLP-Projekt", "Sprachmodelle", "Fine-Tuning", "Evaluation", "Präsentation" } },
        // Semester 5
        new() { Id = 21, Semester = 5, Name = "Projekt: Edge AI",                                                           Code = "EA-501P",   Color = "#6C5CE7", Icon = "⚡",  Ects = 5,  Topics = new() { "Edge Computing", "TinyML", "Embedded AI", "Optimierung", "Deployment" } },
        new() { Id = 22, Semester = 5, Name = "Seminar: Ethische Innovation",                                               Code = "EI-501",    Color = "#A29BFE", Icon = "◌",   Ects = 5,  Topics = new() { "Innovationsethik", "Nachhaltigkeit", "Gesellschaft & Technik", "Diskurs", "Seminar" } },
        // Semester 5 – Wahlpflichtmodule A (5 ECTS)
        new() { Id = 24, Semester = 5, Group = "Wahlpflichtmodule A (5 ECTS)", Name = "Einführung in die Robotik",          Code = "RO-501",    Color = "#E17055", Icon = "🦾",  Ects = 5,  Topics = new() { "Roboterkinematik", "Sensorik", "Aktuatoren", "Programmierung", "ROS" } },
        new() { Id = 25, Semester = 5, Group = "Wahlpflichtmodule A (5 ECTS)", Name = "Mechanik - Kinematik",               Code = "MK-501",    Color = "#00CEC9", Icon = "⚙",   Ects = 5,  Topics = new() { "Kinematik", "Dynamik", "Starrkörper", "Bewegungsgleichungen", "Simulation" } },
        new() { Id = 26, Semester = 5, Group = "Wahlpflichtmodule A (5 ECTS)", Name = "Augmented, Mixed, und Virtual Reality", Code = "XR-501", Color = "#6C5CE7", Icon = "🥽",  Ects = 5,  Topics = new() { "AR", "VR", "MR", "Unity", "Interaktion" } },
        new() { Id = 27, Semester = 5, Group = "Wahlpflichtmodule A (5 ECTS)", Name = "Data Engineering",                   Code = "DE-501",    Color = "#00B894", Icon = "🔧",  Ects = 5,  Topics = new() { "Datenpipelines", "ETL", "Spark", "Kafka", "Datenarchitektur" } },
        new() { Id = 28, Semester = 5, Group = "Wahlpflichtmodule A (5 ECTS)", Name = "IT-Architekturmanagement",           Code = "AM-501",    Color = "#0984E3", Icon = "🏗",   Ects = 5,  Topics = new() { "Enterprise Architecture", "TOGAF", "Microservices", "Systemdesign", "Governance" } },
        new() { Id = 29, Semester = 5, Group = "Wahlpflichtmodule A (5 ECTS)", Name = "Ethik und Nachhaltigkeit in der IT", Code = "EN-501",   Color = "#FDCB6E", Icon = "🌱",  Ects = 5,  Topics = new() { "IT-Ethik", "Green IT", "Nachhaltigkeit", "Gesellschaft", "Verantwortung" } },
        new() { Id = 30, Semester = 5, Group = "Wahlpflichtmodule A (5 ECTS)", Name = "Data Quality and Data Wrangling",    Code = "DQ-501",    Color = "#55EFC4", Icon = "🧹",  Ects = 5,  Topics = new() { "Datenqualität", "Bereinigung", "Transformation", "Validierung", "Profiling" } },
        new() { Id = 31, Semester = 5, Group = "Wahlpflichtmodule A (5 ECTS)", Name = "Fertigungsverfahren Industrie 4.0",  Code = "FI-501",    Color = "#E84393", Icon = "🏭",  Ects = 5,  Topics = new() { "Industrie 4.0", "Fertigung", "CPS", "IoT", "Smart Factory" } },
        // Semester 5 – Wahlpflichtmodule B (10 ECTS)
        new() { Id = 33, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Embedded Systems",                  Code = "ES-501",    Color = "#D63031", Icon = "💾",  Ects = 5,  Topics = new() { "Mikrocontroller", "RTOS", "Hardware-Programmierung", "Interrupts", "Peripherie" } },
        new() { Id = 34, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "User Experience",                   Code = "UX-501",    Color = "#FD79A8", Icon = "🎨",  Ects = 5,  Topics = new() { "UX-Design", "Usability", "Prototyping", "User Research", "Evaluation" } },
        new() { Id = 35, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "UX-Projekt",                        Code = "UX-502P",   Color = "#E84393", Icon = "🖌",   Ects = 5,  Topics = new() { "UX-Projekt", "Gestaltung", "Testing", "Iteration", "Präsentation" } },
        new() { Id = 36, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Project: AWS - Cloud Essentials",   Code = "AWS-501P",  Color = "#FDCB6E", Icon = "☁",   Ects = 5,  Topics = new() { "AWS Grundlagen", "EC2", "S3", "IAM", "Netzwerk" } },
        new() { Id = 37, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Project: AWS - Cloud Advanced",     Code = "AWS-502P",  Color = "#E17055", Icon = "🚀",  Ects = 5,  Topics = new() { "AWS Advanced", "Lambda", "RDS", "CloudFormation", "Security" } },
        new() { Id = 38, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Experience Psychology",             Code = "EP-501",    Color = "#A29BFE", Icon = "🧠",  Ects = 5,  Topics = new() { "Wahrnehmung", "Kognition", "Emotion", "User Behavior", "Motivation" } },
        new() { Id = 39, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Human-Computer Interaction",        Code = "HCI-501",   Color = "#00CEC9", Icon = "🖥",   Ects = 5,  Topics = new() { "Interaktionsdesign", "Accessibility", "Evaluation", "Menüführung", "Gestaltgesetze" } },
        new() { Id = 40, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Business Intelligence",             Code = "BI-501",    Color = "#00B894", Icon = "📊",  Ects = 5,  Topics = new() { "BI-Grundlagen", "Data Warehouse", "Dashboards", "KPIs", "Reporting" } },
        new() { Id = 41, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Projekt Business Intelligence",     Code = "BI-502P",   Color = "#55EFC4", Icon = "📈",  Ects = 5,  Topics = new() { "BI-Projekt", "Analyse", "Visualisierung", "Reporting", "Präsentation" } },
        new() { Id = 42, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Mobile Robotik",                    Code = "MR-501",    Color = "#0984E3", Icon = "🤖",  Ects = 5,  Topics = new() { "Autonome Systeme", "Navigation", "SLAM", "Pfadplanung", "Sensorfusion" } },
        new() { Id = 43, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Projekt: X-Reality",                Code = "XR-502P",   Color = "#6C5CE7", Icon = "🌐",  Ects = 5,  Topics = new() { "XR-Projekt", "AR/VR-Entwicklung", "3D-Modellierung", "Interaktion", "Präsentation" } },
        new() { Id = 44, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Automatisierung und Robotics",      Code = "AR-501",    Color = "#E17055", Icon = "⚙",   Ects = 5,  Topics = new() { "Automatisierung", "SPS", "Roboterprogrammierung", "Industrie 4.0", "Aktorik" } },
        new() { Id = 45, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Digitale Signalverarbeitung",       Code = "DSV-501",   Color = "#0984E3", Icon = "📡",  Ects = 5,  Topics = new() { "Fourier-Transformation", "Filter", "Abtastung", "FFT", "Signalanalyse" } },
        new() { Id = 46, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Sensorik",                          Code = "SE-501",    Color = "#00B894", Icon = "📡",  Ects = 5,  Topics = new() { "Sensortechnologie", "Messtechnik", "Kalibrierung", "IoT-Sensoren", "Datenerfassung" } },
        new() { Id = 47, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Datenmodellierung und Datenbanksysteme", Code = "DB-501", Color = "#6C5CE7", Icon = "🗄",  Ects = 5,  Topics = new() { "ER-Modell", "Normalisierung", "SQL", "NoSQL", "Datenbankdesign" } },
        new() { Id = 48, Semester = 5, Group = "Wahlpflichtmodule B (10 ECTS)", Name = "Big-Data-Technologien",             Code = "BD-501",    Color = "#D63031", Icon = "💾",  Ects = 5,  Topics = new() { "Hadoop", "Spark", "Datenströme", "Skalierung", "Big-Data-Architektur" } },
        // Semester 6
        new() { Id = 49, Semester = 6, Name = "Model Engineering",                                                          Code = "ME-601",    Color = "#E84393", Icon = "🔧",  Ects = 5,  Topics = new() { "MLOps", "Modellversionierung", "Monitoring", "Reproduzierbarkeit", "Deployment" } },
        new() { Id = 50, Semester = 6, Name = "Bachelorarbeit",                                                             Code = "BA-601",    Color = "#E17055", Icon = "📝",  Ects = 10, Topics = new() { "Forschungsfrage", "Methodologie", "Implementierung", "Evaluation", "Abgabe" } },
        // Semester 6 – Wahlpflichtmodule C (10 ECTS)
        new() { Id = 52, Semester = 6, Group = "Wahlpflichtmodule C (10 ECTS)", Name = "Studium Generale I",                Code = "SG-601",    Color = "#00CEC9", Icon = "📖",  Ects = 5,  Topics = new() { "Fächerübergreifend", "Allgemeinbildung", "Soft Skills", "Interdisziplinär" } },
        new() { Id = 53, Semester = 6, Group = "Wahlpflichtmodule C (10 ECTS)", Name = "Studium Generale II",               Code = "SG-602",    Color = "#55EFC4", Icon = "📖",  Ects = 5,  Topics = new() { "Fächerübergreifend", "Allgemeinbildung", "Soft Skills", "Interdisziplinär" } },
        // Semester 6 – Wahlpflichtmodule D (30 ECTS)
        new() { Id = 55, Semester = 6, Group = "Wahlpflichtmodule D (30 ECTS)", Name = "Praktikum: Bachelor Data Science und KI", Code = "PR-601", Color = "#FDCB6E", Icon = "🏢", Ects = 30, Topics = new() { "Praxisprojekt", "Unternehmen", "Data Science", "KI", "Abschlusspräsentation" } },
        new() { Id = 56, Semester = 6, Group = "Wahlpflichtmodule D (30 ECTS)", Name = "Kollaboratives Arbeiten",           Code = "KA-601",    Color = "#00B894", Icon = "🤝",  Ects = 5,  Topics = new() { "Teamwork", "Agile", "Kommunikation", "Kollaboration", "Projektmanagement" } },
        new() { Id = 57, Semester = 6, Group = "Wahlpflichtmodule D (30 ECTS)", Name = "Projekt: KI-Exzellenz mit kreativen Prompt-Techniken", Code = "PT-601P", Color = "#A29BFE", Icon = "💡", Ects = 5, Topics = new() { "Prompt Engineering", "Generative KI", "Kreativität", "LLMs", "Projektarbeit" } },
        new() { Id = 58, Semester = 6, Group = "Wahlpflichtmodule D (30 ECTS)", Name = "Digitale Business-Modelle",         Code = "BM-601",    Color = "#0984E3", Icon = "💼",  Ects = 5,  Topics = new() { "Geschäftsmodelle", "Digitalisierung", "Plattformökonomie", "Innovation", "Strategie" } },
        new() { Id = 59, Semester = 6, Group = "Wahlpflichtmodule D (30 ECTS)", Name = "Projekt: Digitale Business-Modelle", Code = "BM-602P",  Color = "#6C5CE7", Icon = "💼",  Ects = 5,  Topics = new() { "Business-Modell-Projekt", "Geschäftskonzept", "Pitch", "Umsetzung", "Präsentation" } },
        new() { Id = 60, Semester = 6, Group = "Wahlpflichtmodule D (30 ECTS)", Name = "Projekt: Generative KI im Unternehmenskontext", Code = "GK-601P", Color = "#FD79A8", Icon = "🤖", Ects = 5, Topics = new() { "Generative KI", "Unternehmensanwendung", "LLMs", "Integration", "Projektarbeit" } },
        new() { Id = 61, Semester = 6, Group = "Wahlpflichtmodule D (30 ECTS)", Name = "Personal Skills",                   Code = "PS-601",    Color = "#55EFC4", Icon = "🌟",  Ects = 5,  Topics = new() { "Soft Skills", "Kommunikation", "Zeitmanagement", "Präsentation", "Persönlichkeit" } },
        new() { Id = 62, Semester = 6, Group = "Wahlpflichtmodule D (30 ECTS)", Name = "Projekt: AI Fluency - Einführung in die generative KI", Code = "AF-601P", Color = "#E84393", Icon = "🤖", Ects = 5, Topics = new() { "AI Fluency", "Generative KI", "Grundlagen", "Anwendung", "Projektarbeit" } },
    };

    // ── Additive extensions for custom study programs ──────────────
    // Everything from here on is NEW and purely additive: the static members above
    // (AppliedAICourses, GroupEctsQuotas, the parameterless Calc overloads)
    // remain unchanged, so existing consumers (including PlannerController)
    // keep working exactly as before.

    /// <summary>
    /// Display name of the built-in study program. Custom study programs
    /// live in the DB (StudyProgramEntity); the built-in one has no DB row and is
    /// represented via UserSettings.ActiveStudyProgramId == null.
    /// </summary>
    public const string BuiltInProgramName = "Applied Artificial Intelligence";

    /// <summary>
    /// Program-aware variant of <see cref="CalcTotalEcts(IEnumerable{CourseDto})"/>:
    /// calculates using an explicitly passed quota dictionary instead of the static
    /// <see cref="GroupEctsQuotas"/>. Groups without a quota entry count in full.
    /// </summary>
    public static int CalcTotalEcts(IEnumerable<CourseDto> courses, IReadOnlyDictionary<string, int> groupEctsQuotas)
    {
        var courseList = courses.ToList();
        var ungrouped = courseList.Where(c => c.Group == null).Sum(c => c.Ects);
        var grouped = courseList
            .Where(c => c.Group != null)
            .GroupBy(c => c.Group!)
            .Sum(g => groupEctsQuotas.TryGetValue(g.Key, out var q) ? q : g.Sum(c => c.Ects));
        return ungrouped + grouped;
    }

    /// <summary>
    /// Program-aware variant of <see cref="CalcEctsEarned(IEnumerable{CourseDto}, IEnumerable{int})"/>:
    /// calculates using an explicitly passed quota dictionary instead of the static
    /// <see cref="GroupEctsQuotas"/>. Groups without a quota entry count in full.
    /// </summary>
    public static int CalcEctsEarned(IEnumerable<CourseDto> courses, IEnumerable<int> completedIds, IReadOnlyDictionary<string, int> groupEctsQuotas)
    {
        var completed = new HashSet<int>(completedIds);
        var courseList = courses.ToList();

        var ungrouped = courseList
            .Where(c => c.Group == null && completed.Contains(c.Id))
            .Sum(c => c.Ects);

        var grouped = courseList
            .Where(c => c.Group != null && completed.Contains(c.Id))
            .GroupBy(c => c.Group!)
            .Sum(g =>
            {
                var earned = g.Sum(c => c.Ects);
                var quota = groupEctsQuotas.TryGetValue(g.Key, out var q) ? q : earned;
                return Math.Min(earned, quota);
            });

        return ungrouped + grouped;
    }
}
