namespace LectureAssistant.Core.Captions;

/// <param name="Id">Stable id saved in projects and settings.</param>
/// <param name="DisplayName">Shown to the instructor.</param>
/// <param name="Prompt">
/// A sentence written like a transcript of this kind of lecture, full of correctly spelled terms.
/// Whisper treats it as the speech that came just before the recording, which nudges it toward
/// these spellings (and toward punctuated, capitalized sentences).
/// </param>
public sealed record SubjectArea(string Id, string DisplayName, string Prompt);

/// <summary>The subjects an instructor can pick so captions spell the field's terms correctly.</summary>
public static class SubjectAreas
{
    public static SubjectArea General { get; } = new("general", "General",
        "Good morning, everyone. In today's lecture, we'll go over the key ideas, work through a few examples, and answer your questions.");

    public static IReadOnlyList<SubjectArea> All { get; } =
    [
        General,
        new("software", "Software development",
            "Today we'll build a REST API in Python and C#, store data in PostgreSQL, push to GitHub, and deploy with Docker and Kubernetes. We'll also cover JavaScript, TypeScript, React, and unit tests."),
        new("computing", "Computer science and IT",
            "In this lecture we cover algorithms, Big-O notation, binary trees, hash tables, TCP/IP, DNS, subnets, Linux, SQL databases, virtualization, and cybersecurity basics like encryption and firewalls."),
        new("medicine", "Nursing and medicine",
            "Today we'll assess the patient's vital signs, tachycardia, hypertension, and dyspnea, then review metoprolol, acetaminophen, and heparin dosing, sepsis protocols, and charting in the EHR."),
        new("biology", "Biology",
            "Today we'll look at mitochondria, ribosomes, and the endoplasmic reticulum, then DNA replication, mRNA transcription, mitosis and meiosis, photosynthesis, CRISPR-Cas9, and Escherichia coli."),
        new("chemistry", "Chemistry",
            "Today we'll balance equations, calculate molarity, and work through stoichiometry, covalent bonds, electronegativity, enthalpy, Le Chatelier's principle, titration, and benzene rings."),
        new("physics", "Physics",
            "Today we'll cover Newton's laws, kinetic energy, momentum, and torque, then electromagnetism, Maxwell's equations, the Schrödinger equation, and units like joules, newtons, and pascals."),
        new("math", "Mathematics and statistics",
            "Today we'll work through derivatives, integrals, matrices, and eigenvalues, then the standard deviation, p-values, the null hypothesis, linear regression, Bayes' theorem, and a chi-squared test."),
        new("engineering", "Engineering",
            "Today we'll analyze a truss for tensile and compressive stress, then cover Young's modulus, the Reynolds number, heat transfer, Ohm's law, op-amps, and modeling in MATLAB and SolidWorks."),
        new("business", "Business and accounting",
            "Today we'll read a balance sheet and income statement, then cover accounts receivable, accrual accounting, depreciation, EBITDA, GAAP and IFRS, ROI, cash flow, and the general ledger."),
        new("economics", "Economics",
            "Today we'll cover supply and demand, price elasticity, marginal cost, GDP, inflation, the Federal Reserve, monetary and fiscal policy, Keynesian economics, and comparative advantage."),
        new("law", "Law",
            "Today we'll discuss torts, negligence, and liability, then due process, habeas corpus, stare decisis, mens rea, the plaintiff and defendant, and Marbury v. Madison."),
        new("psychology", "Psychology",
            "Today we'll discuss cognitive behavioral therapy, classical and operant conditioning, Pavlov and Skinner, Piaget's stages, the DSM-5, and neurotransmitters like serotonin and dopamine."),
        new("education", "Education",
            "Today we'll talk about lesson planning, Bloom's taxonomy, formative and summative assessment, differentiated instruction, scaffolding, Vygotsky's zone of proximal development, and IEPs."),
        new("history", "History and social sciences",
            "Today we'll cover the Industrial Revolution, the Treaty of Versailles, the Ottoman Empire, colonialism, and the Cold War, then ideas from Durkheim, Weber, and Marx."),
        new("literature", "Literature and writing",
            "Today we'll read Shakespeare's sonnets and Toni Morrison's Beloved, looking at metaphor, iambic pentameter, allusion, the narrator's point of view, and how to write a thesis statement."),
    ];

    /// <summary>The subject with this id, or <see cref="General"/> when it's missing or unknown.</summary>
    public static SubjectArea Find(string? id) => All.FirstOrDefault(s => s.Id == id) ?? General;
}
