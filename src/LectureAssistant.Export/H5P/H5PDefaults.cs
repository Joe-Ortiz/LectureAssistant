using System.Text.Json.Nodes;

namespace LectureAssistant.Export.H5P;

/// <summary>
/// Default params for each library, taken from the "default" values in its semantics.json
/// (h5p-interactive-video 1.28, h5p-multi-choice 1.16, h5p-true-false 1.8, h5p-blanks 1.14, master branch).
/// The players don't reliably fill in missing UI strings, so every localizable string is emitted explicitly.
/// Content fields (question text, answers, video) are filled in by <see cref="H5PContentBuilder"/>.
/// Each call returns a fresh object that the caller may modify.
/// </summary>
internal static class H5PDefaults
{
    public static JsonObject InteractiveVideo() => Parse("""
        {
          "interactiveVideo": {
            "video": {
              "startScreenOptions": { "title": "Interactive Video", "hideStartTitle": false },
              "textTracks": { "videoTrack": [] }
            },
            "assets": { "interactions": [], "bookmarks": [], "endscreens": [] },
            "summary": { "displayAt": 3 }
          },
          "override": {
            "autoplay": false,
            "loop": false,
            "hasNoAutoPause": false,
            "showBookmarksmenuOnLoad": false,
            "showRewind10": false,
            "preventSkippingMode": "none",
            "deactivateSound": false
          },
          "l10n": {
            "interaction": "Interaction",
            "play": "Play",
            "pause": "Pause",
            "mute": "Mute, currently unmuted",
            "unmute": "Unmute, currently muted",
            "quality": "Video Quality",
            "captions": "Captions",
            "close": "Close",
            "fullscreen": "Fullscreen",
            "exitFullscreen": "Exit Fullscreen",
            "summary": "Open summary dialog",
            "bookmarks": "Bookmarks",
            "endscreen": "Submit screen",
            "defaultAdaptivitySeekLabel": "Continue",
            "continueWithVideo": "Continue with video",
            "more": "More player options",
            "playbackRate": "Playback Rate",
            "rewind10": "Rewind 10 Seconds",
            "navDisabled": "Navigation is disabled",
            "navForwardDisabled": "Navigating forward is disabled",
            "sndDisabled": "Sound is disabled",
            "requiresCompletionWarning": "You need to answer all the questions correctly before continuing.",
            "back": "Back",
            "hours": "Hours",
            "minutes": "Minutes",
            "seconds": "Seconds",
            "currentTime": "Current time:",
            "totalTime": "Total time:",
            "singleInteractionAnnouncement": "Interaction appeared:",
            "multipleInteractionsAnnouncement": "Multiple interactions appeared.",
            "videoPausedAnnouncement": "Video is paused",
            "content": "Content",
            "answered": "@answered answered",
            "endcardTitle": "@answered Question(s) answered",
            "endcardInformation": "You have answered @answered questions, click below to submit your answers.",
            "endcardInformationOnSubmitButtonDisabled": "You have answered @answered questions.",
            "endcardInformationNoAnswers": "You have not answered any questions.",
            "endcardInformationMustHaveAnswer": "You have to answer at least one question before you can submit your answers.",
            "endcardSubmitButton": "Submit Answers",
            "endcardSubmitMessage": "Your answers have been submitted!",
            "endcardTableRowScore": "Score",
            "endcardQuestion": "Question",
            "endcardAnsweredScore": "answered",
            "endCardTableRowSummaryWithScore": "You got @score out of @total points for the @question that appeared after @minutes minutes and @seconds seconds.",
            "endCardTableRowSummaryWithoutScore": "You have answered the @question that appeared after @minutes minutes and @seconds seconds.",
            "videoProgressBar": "Video progress",
            "howToCreateInteractions": "Play the video to start creating interactions"
          }
        }
        """);

    /// <summary>Per-interaction wrapper fields that aren't question-specific.</summary>
    public static JsonObject Interaction() => Parse("""
        {
          "pause": true,
          "displayType": "poster",
          "buttonOnMobile": true,
          "label": "",
          "visuals": { "backgroundColor": "rgb(255, 255, 255)", "boxShadow": true },
          "goto": { "url": { "protocol": "http://" }, "visualize": false },
          "adaptivity": {
            "correct": { "allowOptOut": false, "message": "" },
            "wrong": { "allowOptOut": false, "message": "" },
            "requireCompletion": false
          }
        }
        """);

    public static JsonObject MultiChoice() => Parse("""
        {
          "media": { "disableImageZooming": false },
          "UI": {
            "checkAnswerButton": "Check",
            "submitAnswerButton": "Submit",
            "showSolutionButton": "Show solution",
            "tryAgainButton": "Retry",
            "tipsLabel": "Show tip",
            "scoreBarLabel": "You got :num out of :total points",
            "tipAvailable": "Tip available",
            "feedbackAvailable": "Feedback available",
            "readFeedback": "Read feedback",
            "wrongAnswer": "Wrong answer",
            "correctAnswer": "Correct answer",
            "shouldCheck": "Should have been checked",
            "shouldNotCheck": "Should not have been checked",
            "noInput": "Please answer before viewing the solution",
            "a11yCheck": "Check the answers. The responses will be marked as correct, incorrect, or unanswered.",
            "a11yShowSolution": "Show the solution. The task will be marked with its correct solution.",
            "a11yRetry": "Retry the task. Reset all responses and start the task over again."
          },
          "behaviour": {
            "enableRetry": true,
            "enableSolutionsButton": true,
            "enableCheckButton": true,
            "type": "auto",
            "singlePoint": false,
            "randomAnswers": true,
            "showSolutionsRequiresInput": true,
            "confirmCheckDialog": false,
            "confirmRetryDialog": false,
            "autoCheck": false,
            "passPercentage": 100,
            "showScorePoints": true
          },
          "confirmCheck": { "header": "Finish ?", "body": "Are you sure you wish to finish ?", "cancelLabel": "Cancel", "confirmLabel": "Finish" },
          "confirmRetry": { "header": "Retry ?", "body": "Are you sure you wish to retry ?", "cancelLabel": "Cancel", "confirmLabel": "Confirm" }
        }
        """);

    public static JsonObject TrueFalse() => Parse("""
        {
          "media": { "disableImageZooming": false },
          "correct": "true",
          "l10n": {
            "trueText": "True",
            "falseText": "False",
            "score": "You got @score of @total points",
            "checkAnswer": "Check",
            "submitAnswer": "Submit",
            "showSolutionButton": "Show solution",
            "tryAgain": "Retry",
            "wrongAnswerMessage": "Wrong answer",
            "correctAnswerMessage": "Correct answer",
            "scoreBarLabel": "You got :num out of :total points",
            "a11yCheck": "Check the answers. The responses will be marked as correct, incorrect, or unanswered.",
            "a11yShowSolution": "Show the solution. The task will be marked with its correct solution.",
            "a11yRetry": "Retry the task. Reset all responses and start the task over again."
          },
          "behaviour": {
            "enableRetry": true,
            "enableSolutionsButton": true,
            "enableCheckButton": true,
            "confirmCheckDialog": false,
            "confirmRetryDialog": false,
            "autoCheck": false
          },
          "confirmCheck": { "header": "Finish ?", "body": "Are you sure you wish to finish ?", "cancelLabel": "Cancel", "confirmLabel": "Finish" },
          "confirmRetry": { "header": "Retry ?", "body": "Are you sure you wish to retry ?", "cancelLabel": "Cancel", "confirmLabel": "Confirm" }
        }
        """);

    public static JsonObject Blanks() => Parse("""
        {
          "media": { "disableImageZooming": false },
          "text": "Fill in the missing words",
          "showSolutions": "Show solution",
          "tryAgain": "Retry",
          "checkAnswer": "Check",
          "submitAnswer": "Submit",
          "notFilledOut": "Please fill in all blanks to view solution",
          "answerIsCorrect": "':ans' is correct",
          "answerIsWrong": "':ans' is wrong",
          "answeredCorrectly": "Answered correctly",
          "answeredIncorrectly": "Answered incorrectly",
          "solutionLabel": "Correct answer:",
          "inputLabel": "Blank input @num of @total",
          "inputHasTipLabel": "Tip available",
          "tipLabel": "Tip",
          "behaviour": {
            "enableRetry": true,
            "allowRetryIfCorrect": false,
            "enableSolutionsButton": true,
            "enableCheckButton": true,
            "autoCheck": false,
            "caseSensitive": true,
            "showSolutionsRequiresInput": true,
            "separateLines": false,
            "confirmCheckDialog": false,
            "confirmRetryDialog": false,
            "acceptSpellingErrors": false
          },
          "confirmCheck": { "header": "Finish ?", "body": "Are you sure you wish to finish ?", "cancelLabel": "Cancel", "confirmLabel": "Finish" },
          "confirmRetry": { "header": "Retry ?", "body": "Are you sure you wish to retry ?", "cancelLabel": "Cancel", "confirmLabel": "Confirm" },
          "scoreBarLabel": "You got :num out of :total points",
          "a11yCheck": "Check the answers. The responses will be marked as correct, incorrect, or unanswered.",
          "a11yShowSolution": "Show the solution. The task will be marked with its correct solution.",
          "a11yRetry": "Retry the task. Reset all responses and start the task over again.",
          "a11yCheckingModeHeader": "Checking mode"
        }
        """);

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}
