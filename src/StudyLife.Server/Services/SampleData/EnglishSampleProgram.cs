// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;

namespace StudyLife.Server.Services.SampleData;

/// <summary>
/// The study program of the English sample dataset: an English counterpart of the built-in
/// "Applied Artificial Intelligence" catalog (whose course names and topics are German), created
/// as the account's own custom program. Semesters 1 and 2 mirror the built-in catalog course by
/// course (same order, codes, colors and icons, and five courses each with the project module
/// last), because <see cref="SampleDataset"/> derives the completed and active courses from them.
/// Semesters 3-6 are the mandatory modules plus one choice per elective group of the built-in
/// catalog. The credits mirror what the built-in catalog credits per semester (5 per module,
/// 25 in semesters 1-5, 55 in semester 6 with the 30-ECTS internship and the 10-ECTS thesis),
/// 180 in total, so the English and the German sample datasets show the same ECTS, progress and
/// forecast. Ids are assigned by the database; topics must not contain commas (they are stored
/// comma-separated).
/// </summary>
public static class EnglishSampleProgram
{
    public const string Name = "Applied Artificial Intelligence (B.Sc.)";

    public static readonly SampleProgram Program = new(Name,
    [
        // Semester 1
        Course(1, "Artificial Intelligence", "AI-101", "#6C5CE7", "✦", 5, "AI fundamentals", "Search algorithms", "Knowledge representation", "Planning", "AI overview"),
        Course(1, "Introduction to Programming with Python", "PY-101", "#00B894", "🐍", 5, "Syntax", "Data types", "Control flow", "Functions", "OOP basics"),
        Course(1, "Mathematics: Calculus", "MA-101", "#0984E3", "∫", 5, "Limits", "Derivatives", "Integrals", "Series", "Differential equations"),
        Course(1, "Introduction to Academic Work in IT and Engineering", "WA-101", "#FDCB6E", "📄", 5, "Literature research", "Citing sources", "Methodology", "Philosophy of science", "Academic writing"),
        Course(1, "Project: Object-Oriented and Functional Programming with Python", "PY-102P", "#E17055", "⚙", 5, "OOP", "Functional programming", "Lambdas", "Decorators", "Project work"),
        // Semester 2
        Course(2, "Mathematics: Linear Algebra", "MA-201", "#6C5CE7", "∑", 5, "Vectors", "Matrices", "Eigenvalues", "Systems of linear equations", "Linear transformations"),
        Course(2, "Statistics: Probability and Descriptive Statistics", "ST-201", "#00B894", "📊", 5, "Probability", "Distributions", "Mean", "Variance", "Data visualization"),
        Course(2, "Statistics: Inferential Statistics", "ST-202", "#0984E3", "📈", 5, "Hypothesis testing", "Confidence intervals", "Regression", "ANOVA", "Estimation"),
        Course(2, "Cloud Computing", "CC-201", "#FDCB6E", "☁", 5, "AWS", "Azure", "GCP", "Serverless", "IaaS / PaaS / SaaS"),
        Course(2, "Project: Cloud Programming", "CC-202P", "#E17055", "🚀", 5, "Cloud architecture", "Deployment", "Containers", "CI/CD", "Project work"),
        // Semester 3
        Course(3, "Machine Learning: Supervised Learning", "ML-301", "#E84393", "🤖", 5, "Regression", "Classification", "Support vector machines", "Decision trees", "Model evaluation"),
        Course(3, "Machine Learning: Unsupervised Learning and Feature Engineering", "ML-302", "#A29BFE", "🔍", 5, "Clustering", "PCA", "Dimensionality reduction", "Feature engineering", "Anomaly detection"),
        Course(3, "Neural Networks and Deep Learning", "DL-301", "#6C5CE7", "🧠", 5, "Neural networks", "CNNs", "RNNs", "Backpropagation", "Frameworks"),
        Course(3, "Introduction to Computer Vision", "CV-301", "#00CEC9", "👁", 5, "Image processing", "Object detection", "Segmentation", "OpenCV", "GANs"),
        Course(3, "Project: Computer Vision", "CV-302P", "#55EFC4", "📷", 5, "Computer vision project", "Data pipeline", "Model training", "Deployment", "Presentation"),
        // Semester 4
        Course(4, "Introduction to Reinforcement Learning", "RL-401", "#E17055", "🎮", 5, "Markov decision processes", "Q-learning", "Policy gradients", "Actor-critic methods", "Simulation environments"),
        Course(4, "Data Protection and IT Security", "DS-401", "#FDCB6E", "🔒", 5, "GDPR", "IT security", "Cryptography", "Attacks", "Privacy law"),
        Course(4, "Ethical and Legal Aspects of AI", "ET-401", "#FD79A8", "⚖", 5, "AI ethics", "Bias and fairness", "Regulation", "Accountability", "Explainability"),
        Course(4, "Introduction to Natural Language Processing", "NLP-401", "#00B894", "💬", 5, "Tokenization", "Embeddings", "BERT", "GPT", "Sentiment analysis"),
        Course(4, "Project: Natural Language Processing", "NLP-402P", "#0984E3", "✍", 5, "NLP project", "Language models", "Fine-tuning", "Evaluation", "Presentation"),
        // Semester 5
        Course(5, "Project: Edge AI", "EA-501P", "#6C5CE7", "⚡", 5, "Edge computing", "TinyML", "Embedded AI", "Optimization", "Deployment"),
        Course(5, "Seminar: Ethical Innovation", "EI-501", "#A29BFE", "◌", 5, "Innovation ethics", "Sustainability", "Technology and society", "Discourse", "Seminar paper"),
        Course(5, "Data Engineering", "DE-501", "#00B894", "🔧", 5, "Data pipelines", "ETL", "Spark", "Kafka", "Data architecture"),
        Course(5, "Human-Computer Interaction", "HCI-501", "#00CEC9", "🖥", 5, "Interaction design", "Accessibility", "Usability evaluation", "Navigation design", "Gestalt principles"),
        Course(5, "Business Intelligence", "BI-501", "#0984E3", "📊", 5, "BI fundamentals", "Data warehousing", "Dashboards", "KPIs", "Reporting"),
        // Semester 6
        Course(6, "Model Engineering", "ME-601", "#E84393", "🔧", 5, "MLOps", "Model versioning", "Monitoring", "Reproducibility", "Deployment"),
        Course(6, "General Studies I", "SG-601", "#00CEC9", "🎓", 5, "Interdisciplinary topics", "Communication", "Self-management", "Teamwork", "Reflection"),
        Course(6, "General Studies II", "SG-602", "#55EFC4", "🎓", 5, "Interdisciplinary topics", "Presentation skills", "Project management", "Intercultural skills", "Reflection"),
        Course(6, "Internship", "IN-601", "#FDCB6E", "🏢", 30, "Practical project", "Company onboarding", "Data science in practice", "Applied AI", "Final presentation"),
        Course(6, "Bachelor's Thesis", "BA-601", "#E17055", "📝", 10, "Research question", "Methodology", "Implementation", "Evaluation", "Submission"),
    ]);

    private static CourseDto Course(int semester, string name, string code, string color, string icon, int ects, params string[] topics) =>
        new() { Semester = semester, Name = name, Code = code, Color = color, Icon = icon, Ects = ects, Topics = [.. topics] };
}
