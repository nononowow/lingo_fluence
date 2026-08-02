using LingoFluence.Models;

namespace LingoFluence.Services;

/// <summary>
/// SM-2 spaced repetition algorithm with Anki-style modifications.
/// </summary>
public class SpacedRepetitionService
{
    private const double MinEaseFactor = 1.3;
    // New cards start with 1-day learning steps
    private static readonly int[] LearningSteps = { 1, 10 }; // minutes

    public void ApplyGrade(Card card, ReviewGrade grade)
    {
        var now = DateTime.Now;

        if (card.State == CardState.New || card.State == CardState.Learning)
        {
            HandleLearning(card, grade, now);
        }
        else // Review
        {
            HandleReview(card, grade, now);
        }
    }

    private void HandleLearning(Card card, ReviewGrade grade, DateTime now)
    {
        switch (grade)
        {
            case ReviewGrade.Again:
                card.State = CardState.Learning;
                card.DueDate = now.AddMinutes(LearningSteps[0]);
                card.Interval = 0;
                break;

            case ReviewGrade.Good:
                if (card.State == CardState.New || card.Interval == 0)
                {
                    // Graduate to review with 1-day interval
                    card.State = CardState.Review;
                    card.Interval = 1;
                    card.DueDate = now.AddDays(1);
                }
                else
                {
                    card.State = CardState.Review;
                    card.Interval = Math.Max(1, (int)(card.Interval * card.EaseFactor));
                    card.DueDate = now.AddDays(card.Interval);
                }
                break;

            case ReviewGrade.Easy:
                card.State = CardState.Review;
                card.Interval = Math.Max(4, (int)(card.Interval * card.EaseFactor * 1.3));
                card.EaseFactor = Math.Min(card.EaseFactor + 0.15, 3.5);
                card.DueDate = now.AddDays(card.Interval);
                break;

            case ReviewGrade.Hard:
                card.State = CardState.Learning;
                card.DueDate = now.AddMinutes(LearningSteps[0]);
                break;
        }
        card.RepCount++;
    }

    private void HandleReview(Card card, ReviewGrade grade, DateTime now)
    {
        switch (grade)
        {
            case ReviewGrade.Again:
                card.LapseCount++;
                card.EaseFactor = Math.Max(MinEaseFactor, card.EaseFactor - 0.20);
                card.State = CardState.Learning;
                card.Interval = Math.Max(1, (int)(card.Interval * 0.5));
                card.DueDate = now.AddMinutes(LearningSteps[0]);
                break;

            case ReviewGrade.Hard:
                card.EaseFactor = Math.Max(MinEaseFactor, card.EaseFactor - 0.15);
                card.Interval = Math.Max(1, (int)(card.Interval * 1.2));
                card.DueDate = now.AddDays(card.Interval);
                break;

            case ReviewGrade.Good:
                card.Interval = Math.Max(1, (int)(card.Interval * card.EaseFactor));
                card.DueDate = now.AddDays(card.Interval);
                break;

            case ReviewGrade.Easy:
                card.EaseFactor = Math.Min(card.EaseFactor + 0.15, 3.5);
                card.Interval = Math.Max(1, (int)(card.Interval * card.EaseFactor * 1.3));
                card.DueDate = now.AddDays(card.Interval);
                break;
        }
        card.RepCount++;
    }

    /// <summary>Returns how many days until the card is due (negative = overdue).</summary>
    public int DaysUntilDue(Card card)
        => (int)(card.DueDate.Date - DateTime.Today).TotalDays;
}
