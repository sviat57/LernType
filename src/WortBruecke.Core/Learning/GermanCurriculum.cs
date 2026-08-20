namespace WortBruecke.Core.Learning;

/// <summary>
/// Baseline A0-C2 curriculum spine. Concrete content can be replaced or expanded while the
/// progression, evidence and exam-readiness contracts remain stable.
/// </summary>
public static class GermanCurriculum
{
    public static IReadOnlyList<GermanLevel> OrderedLevels { get; } = Array.AsReadOnly(Enum.GetValues<GermanLevel>());

    public static LearningPathDefinition CreateDefault() => new(
    [
        Level(
            GermanLevel.A0,
            "A0 · Старт",
            "Узнавать базовые слова и воспроизводить самые короткие бытовые реплики.",
            O(GermanLevel.A0, LanguageSkill.Vocabulary, ExerciseType.ImageAssociation, "core-words", "Первые слова", "Узнавать предметы, числа, людей и действия по слову или изображению."),
            O(GermanLevel.A0, LanguageSkill.Grammar, ExerciseType.SentenceAssembly, "first-patterns", "Базовые модели", "Собирать утвердительную фразу и простой вопрос из готовых частей."),
            O(GermanLevel.A0, LanguageSkill.Reading, ExerciseType.VocabularyRecognition, "labels", "Надписи", "Распознавать имена, страны, числа и частые публичные надписи."),
            O(GermanLevel.A0, LanguageSkill.Listening, ExerciseType.ListeningComprehension, "sounds", "Звуки и приветствия", "Различать буквы, числа, приветствия и короткие инструкции."),
            O(GermanLevel.A0, LanguageSkill.Writing, ExerciseType.GuidedWriting, "personal-data", "Личные данные", "Заполнять имя, страну, язык, адрес и короткую анкету по образцу."),
            O(GermanLevel.A0, LanguageSkill.Speaking, ExerciseType.Pronunciation, "introduce", "Произношение", "Произносить алфавит, приветствия и кратко представляться."),
            O(GermanLevel.A0, LanguageSkill.Mediation, ExerciseType.BidirectionalTranslation, "survival-help", "Языковая помощь", "Передавать значение отдельного бытового слова или знака между русским и немецким.")),
        Level(
            GermanLevel.A1,
            "A1 · Базовое общение",
            "Решать простые повседневные задачи, если собеседник говорит ясно и помогает.",
            O(GermanLevel.A1, LanguageSkill.Vocabulary, ExerciseType.BidirectionalTranslation, "everyday-vocabulary", "Повседневная лексика", "Активно использовать частые слова по темам семья, дом, покупки, время и город."),
            O(GermanLevel.A1, LanguageSkill.Grammar, ExerciseType.GapFill, "simple-grammar", "Простая грамматика", "Применять Präsens, артикли, местоимения, отрицание и базовый порядок слов."),
            O(GermanLevel.A1, LanguageSkill.Reading, ExerciseType.InformationMatching, "short-texts", "Короткие тексты", "Находить конкретную информацию в объявлениях, сообщениях, расписаниях и меню."),
            O(GermanLevel.A1, LanguageSkill.Listening, ExerciseType.ListeningComprehension, "announcements", "Короткие сообщения", "Понимать основную информацию в медленных диалогах и объявлениях."),
            O(GermanLevel.A1, LanguageSkill.Writing, ExerciseType.FunctionalWriting, "message", "Личное сообщение", "Писать короткое сообщение, приглашение или ответ с заданными пунктами."),
            O(GermanLevel.A1, LanguageSkill.Speaking, ExerciseType.Dialogue, "daily-dialogue", "Бытовой диалог", "Представляться, задавать простые вопросы, просить и отвечать в знакомой ситуации."),
            O(GermanLevel.A1, LanguageSkill.Mediation, ExerciseType.MediationSummary, "basic-mediation", "Передача фактов", "Передавать другому человеку ключевые числа, время, место и простые инструкции.")),
        Level(
            GermanLevel.A2,
            "A2 · Самостоятельность в быту",
            "Общаться в типичных ситуациях и описывать личный опыт простыми связанными фразами.",
            O(GermanLevel.A2, LanguageSkill.Vocabulary, ExerciseType.VocabularyRecall, "topic-vocabulary", "Тематическая лексика", "Подбирать слова и устойчивые сочетания для работы, поездок, здоровья и досуга."),
            O(GermanLevel.A2, LanguageSkill.Grammar, ExerciseType.GrammarTransformation, "past-and-clauses", "Прошедшее и придаточные", "Использовать Perfekt, модальные глаголы, падежи и частые придаточные предложения."),
            O(GermanLevel.A2, LanguageSkill.Reading, ExerciseType.ReadingComprehension, "connected-texts", "Связные тексты", "Понимать основную мысль и детали личных писем, инструкций и коротких статей."),
            O(GermanLevel.A2, LanguageSkill.Listening, ExerciseType.NoteTaking, "daily-listening", "Бытовое аудирование", "Выделять тему и записывать нужные факты из разговоров и сообщений."),
            O(GermanLevel.A2, LanguageSkill.Writing, ExerciseType.FunctionalWriting, "request-and-story", "Запрос и рассказ", "Писать связное письмо, запрос или краткий рассказ, покрывая все пункты задания."),
            O(GermanLevel.A2, LanguageSkill.Speaking, ExerciseType.SpokenResponse, "describe-and-agree", "Описание и договорённость", "Описывать событие или изображение и договариваться о простом совместном действии."),
            O(GermanLevel.A2, LanguageSkill.Mediation, ExerciseType.MediationSummary, "practical-mediation", "Практическое посредничество", "Кратко передавать полезную информацию из объявления, письма или инструкции.")),
        Level(
            GermanLevel.B1,
            "B1 · Уверенная самостоятельность",
            "Справляться с большинством знакомых ситуаций и связно объяснять мнение, планы и опыт.",
            O(GermanLevel.B1, LanguageSkill.Vocabulary, ExerciseType.VocabularyRecall, "productive-vocabulary", "Продуктивная лексика", "Выбирать точные слова, синонимы и связки для знакомых общественных тем.", minimumAttempts: 4),
            O(GermanLevel.B1, LanguageSkill.Grammar, ExerciseType.ErrorCorrection, "sentence-control", "Контроль предложения", "Уверенно применять времена, управление, придаточные и распознавать типичные ошибки.", minimumAttempts: 4),
            O(GermanLevel.B1, LanguageSkill.Reading, ExerciseType.ReadingComprehension, "arguments", "Аргументы в тексте", "Понимать структуру, позиции и существенные детали писем, статей и инструкций.", minimumAttempts: 4),
            O(GermanLevel.B1, LanguageSkill.Listening, ExerciseType.NoteTaking, "natural-listening", "Естественная речь", "Понимать основные пункты стандартной речи и фиксировать значимые детали.", minimumAttempts: 4),
            O(GermanLevel.B1, LanguageSkill.Writing, ExerciseType.FunctionalWriting, "structured-letter", "Структурированное письмо", "Писать связное письмо или мнение с вступлением, аргументами и завершением.", minimumAttempts: 4),
            O(GermanLevel.B1, LanguageSkill.Speaking, ExerciseType.OralPresentation, "present-and-discuss", "Сообщение и обсуждение", "Делать подготовленное сообщение, отвечать на вопросы и поддерживать обсуждение.", minimumAttempts: 4),
            O(GermanLevel.B1, LanguageSkill.Mediation, ExerciseType.MediationSummary, "selective-mediation", "Выборочная передача", "Отбирать и понятно передавать адресату релевантные сведения из связного текста.", minimumAttempts: 4)),
        Level(
            GermanLevel.B2,
            "B2 · Свободное взаимодействие",
            "Понимать сложные тексты и уверенно аргументировать без заметного напряжения для собеседника.",
            O(GermanLevel.B2, LanguageSkill.Vocabulary, ExerciseType.IntegratedSkills, "precision-and-register", "Точность и регистр", "Использовать коллокации, перефразирование и подходящий регистр в широком круге тем.", minimumAttempts: 4),
            O(GermanLevel.B2, LanguageSkill.Grammar, ExerciseType.GrammarTransformation, "complex-structures", "Сложные структуры", "Контролировать пассив, Konjunktiv II, относительные и сложные придаточные конструкции.", minimumAttempts: 4),
            O(GermanLevel.B2, LanguageSkill.Reading, ExerciseType.InformationMatching, "dense-reading", "Сложное чтение", "Сопоставлять позиции, выводы и детали в нескольких объёмных аутентичных текстах.", minimumAttempts: 4),
            O(GermanLevel.B2, LanguageSkill.Listening, ExerciseType.ListeningComprehension, "extended-listening", "Развёрнутое аудирование", "Следить за аргументацией в интервью, докладе и дискуссии в стандартном темпе.", minimumAttempts: 4),
            O(GermanLevel.B2, LanguageSkill.Writing, ExerciseType.EssayWriting, "argumentative-writing", "Аргументированный текст", "Развивать позицию, сопоставлять варианты и логично связывать развёрнутый текст.", minimumAttempts: 4),
            O(GermanLevel.B2, LanguageSkill.Speaking, ExerciseType.Dialogue, "debate", "Дискуссия", "Спонтанно излагать и защищать позицию, реагировать на контраргументы и искать решение.", minimumAttempts: 4),
            O(GermanLevel.B2, LanguageSkill.Mediation, ExerciseType.MediationSummary, "audience-mediation", "Медиация для адресата", "Перестраивать содержание сложного источника под цель и знания конкретного адресата.", minimumAttempts: 4)),
        Level(
            GermanLevel.C1,
            "C1 · Продвинутое владение",
            "Гибко и эффективно использовать немецкий в учебной, профессиональной и общественной среде.",
            O(GermanLevel.C1, LanguageSkill.Vocabulary, ExerciseType.IntegratedSkills, "idiomatic-control", "Идиоматическая точность", "Гибко выбирать идиоматику, оттенки значения и регистр, почти не прибегая к поиску слов.", minimumAttempts: 5),
            O(GermanLevel.C1, LanguageSkill.Grammar, ExerciseType.ErrorCorrection, "advanced-control", "Продвинутый контроль", "Стабильно контролировать сложный синтаксис и самостоятельно исправлять редкие ошибки.", minimumAttempts: 5),
            O(GermanLevel.C1, LanguageSkill.Reading, ExerciseType.ReadingComprehension, "implicit-meaning", "Скрытый смысл", "Распознавать имплицитные позиции, тон и композицию сложных академических и публицистических текстов.", minimumAttempts: 5),
            O(GermanLevel.C1, LanguageSkill.Listening, ExerciseType.NoteTaking, "complex-audio", "Сложное аудирование", "Следить за длинной неявно структурированной речью и точно конспектировать аргументы.", minimumAttempts: 5),
            O(GermanLevel.C1, LanguageSkill.Writing, ExerciseType.EssayWriting, "academic-writing", "Академическое письмо", "Создавать ясный, хорошо организованный текст со сложной аргументацией и точным регистром.", minimumAttempts: 5),
            O(GermanLevel.C1, LanguageSkill.Speaking, ExerciseType.OralPresentation, "professional-speaking", "Профессиональная речь", "Свободно представлять сложную тему, управлять структурой и точно отвечать на вопросы.", minimumAttempts: 5),
            O(GermanLevel.C1, LanguageSkill.Mediation, ExerciseType.MediationSummary, "complex-mediation", "Сложная медиация", "Синтезировать несколько источников, пояснять концепции и сохранять важные смысловые нюансы.", minimumAttempts: 5)),
        Level(
            GermanLevel.C2,
            "C2 · Точное владение",
            "Понимать практически всё и выражаться спонтанно, точно и уместно даже в высокосложных ситуациях.",
            O(GermanLevel.C2, LanguageSkill.Vocabulary, ExerciseType.IntegratedSkills, "nuance", "Смысловые нюансы", "Точно управлять коннотациями, идиоматикой, стилем и тонкими различиями значения.", minimumAttempts: 5),
            O(GermanLevel.C2, LanguageSkill.Grammar, ExerciseType.ErrorCorrection, "stylistic-grammar", "Стилистический синтаксис", "Использовать грамматическую вариативность как средство ритма, фокуса и стилистического эффекта.", minimumAttempts: 5),
            O(GermanLevel.C2, LanguageSkill.Reading, ExerciseType.IntegratedSkills, "critical-synthesis", "Критический синтез", "Интерпретировать многослойные тексты разных жанров и сопоставлять их скрытые предпосылки.", minimumAttempts: 5),
            O(GermanLevel.C2, LanguageSkill.Listening, ExerciseType.ListeningComprehension, "unrestricted-listening", "Неограниченное аудирование", "Понимать быструю, вариативную и имплицитную речь, включая незнакомые акценты и регистры.", minimumAttempts: 5),
            O(GermanLevel.C2, LanguageSkill.Writing, ExerciseType.EssayWriting, "expert-writing", "Экспертное письмо", "Создавать точные тексты сложного жанра с эффективной структурой и стилистической гибкостью.", minimumAttempts: 5),
            O(GermanLevel.C2, LanguageSkill.Speaking, ExerciseType.OralPresentation, "expert-speaking", "Экспертное выступление", "Спонтанно перестраивать сложное сообщение и точно передавать тончайшие смысловые различия.", minimumAttempts: 5),
            O(GermanLevel.C2, LanguageSkill.Mediation, ExerciseType.IntegratedSkills, "expert-mediation", "Экспертная медиация", "Синтезировать и переосмыслять сложные источники для разных аудиторий без потери нюансов.", minimumAttempts: 5))
    ]);

    /// <summary>
    /// Creates a conservative provider-neutral four-module profile. A real exam integration should
    /// replace it with a versioned definition matching the selected provider and exam edition.
    /// </summary>
    public static ExamDefinition CreateGenericFourSkillExam(
        string id,
        string title,
        GermanLevel level,
        double minimumSectionScore = 0.6,
        double overallMinimumScore = 0.65) => new(
        id,
        title,
        level,
        [
            Section("reading", "Чтение", LanguageSkill.Reading,
                [ExerciseType.ReadingComprehension, ExerciseType.InformationMatching, ExerciseType.ExamModuleSimulation], minimumSectionScore),
            Section("listening", "Аудирование", LanguageSkill.Listening,
                [ExerciseType.ListeningComprehension, ExerciseType.NoteTaking, ExerciseType.ExamModuleSimulation], minimumSectionScore),
            Section("writing", "Письмо", LanguageSkill.Writing,
                [ExerciseType.FunctionalWriting, ExerciseType.EssayWriting, ExerciseType.ExamModuleSimulation], minimumSectionScore),
            Section("speaking", "Говорение", LanguageSkill.Speaking,
                [ExerciseType.SpokenResponse, ExerciseType.Dialogue, ExerciseType.OralPresentation, ExerciseType.ExamModuleSimulation], minimumSectionScore)
        ],
        overallMinimumScore,
        minimumCompleteMockExams: 2,
        recentEvidenceWindowPerSection: 5);

    private static LevelDefinition Level(
        GermanLevel level,
        string title,
        string outcome,
        params LearningObjective[] objectives) => new(level, title, outcome, objectives);

    private static LearningObjective O(
        GermanLevel level,
        LanguageSkill skill,
        ExerciseType type,
        string key,
        string title,
        string descriptor,
        int minimumAttempts = 3) => new(
            $"{level.ToString().ToLowerInvariant()}.{skill.ToString().ToLowerInvariant()}.{key}",
            level,
            skill,
            type,
            title,
            descriptor,
            minimumAttempts,
            MasteryThreshold(level));

    private static ExamSectionDefinition Section(
        string id,
        string title,
        LanguageSkill skill,
        ExerciseType[] types,
        double score) => new(
            id,
            title,
            [skill],
            types,
            weight: 1,
            minimumScore: score,
            minimumEvidenceCount: 3,
            minimumTimedEvidenceCount: 1);

    private static double MasteryThreshold(GermanLevel level) => level switch
    {
        GermanLevel.A0 => 0.70,
        GermanLevel.A1 => 0.72,
        GermanLevel.A2 => 0.74,
        GermanLevel.B1 => 0.75,
        GermanLevel.B2 => 0.77,
        GermanLevel.C1 => 0.80,
        GermanLevel.C2 => 0.82,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown German learning level.")
    };
}
