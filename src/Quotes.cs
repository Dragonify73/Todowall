using System;

namespace TodoWall
{
    /// <summary>
    /// The line under "Welcome back" on the welcome screen: something short about getting
    /// on with the day. Kept in the program rather than fetched, because a splash that
    /// waits on the network at login is a splash that hangs at login.
    ///
    /// Short lines that have been quoted widely, credited to whoever they are usually
    /// credited to. The welcome screen walks the list in order, one line per greeting,
    /// and keeps its place in the settings file so a restart carries on rather than
    /// starting over - see <see cref="Next"/>.
    /// </summary>
    internal static class Quotes
    {
        internal struct Quote
        {
            public readonly string Text;
            public readonly string By;
            public Quote(string text, string by) { Text = text; By = by; }
        }

        static readonly Quote[] All =
        {
            new Quote("The secret of getting ahead is getting started.", "Mark Twain"),
            new Quote("Well begun is half done.", "Aristotle"),
            new Quote("Do what you can, with what you have, where you are.", "Theodore Roosevelt"),
            new Quote("It is not enough to be busy. The question is: what are we busy about?", "Henry David Thoreau"),
            new Quote("Action is the foundational key to all success.", "Pablo Picasso"),
            new Quote("The future depends on what you do today.", "Mahatma Gandhi"),
            new Quote("How we spend our days is, of course, how we spend our lives.", "Annie Dillard"),
            new Quote("Do the hard jobs first. The easy jobs will take care of themselves.", "Dale Carnegie"),
            new Quote("A journey of a thousand miles begins with a single step.", "Laozi"),
            new Quote("Don't watch the clock; do what it does. Keep going.", "Sam Levenson"),
            new Quote("Whatever you do, do it with all your might.", "Marcus Tullius Cicero"),
            new Quote("Success is the sum of small efforts, repeated day in and day out.", "Robert Collier"),
            new Quote("Lost time is never found again.", "Benjamin Franklin"),
            new Quote("It always seems impossible until it is done.", "Nelson Mandela"),
            new Quote("The best time to plant a tree was twenty years ago. The second best time is now.", "Chinese proverb"),
            new Quote("Either you run the day or the day runs you.", "Jim Rohn"),
            new Quote("Genius is one percent inspiration and ninety-nine percent perspiration.", "Thomas Edison"),
            new Quote("What you do today can improve all your tomorrows.", "Ralph Marston"),
            new Quote("Inspiration exists, but it has to find you working.", "Pablo Picasso"),
            new Quote("The way to get started is to quit talking and begin doing.", "Walt Disney"),
            new Quote("Perseverance is not a long race; it is many short races one after the other.", "Walter Elliot"),
            new Quote("Small deeds done are better than great deeds planned.", "Peter Marshall"),
            new Quote("Until we can manage time, we can manage nothing else.", "Peter Drucker"),
            new Quote("We are what we repeatedly do. Excellence, then, is not an act but a habit.", "Will Durant"),
            new Quote("Make each day your masterpiece.", "John Wooden"),
            new Quote("Amateurs sit and wait for inspiration; the rest of us just get up and go to work.", "Stephen King"),
            new Quote("The man who moves a mountain begins by carrying away small stones.", "Confucius"),
            new Quote("Concentrate all your thoughts upon the work at hand.", "Alexander Graham Bell"),
            new Quote("Procrastination is the thief of time.", "Edward Young"),
            new Quote("You don't have to see the whole staircase, just take the first step.", "Martin Luther King Jr."),
            new Quote("Efficiency is doing things right; effectiveness is doing the right things.", "Peter Drucker"),
            new Quote("Nothing is particularly hard if you divide it into small jobs.", "Henry Ford"),
            new Quote("Be not afraid of going slowly; be afraid only of standing still.", "Chinese proverb"),
            new Quote("The best way out is always through.", "Robert Frost"),
            new Quote("Motivation is what gets you started. Habit is what keeps you going.", "Jim Ryun"),
            new Quote("Begin at once to live, and count each separate day as a separate life.", "Seneca"),
            new Quote("The harder I work, the luckier I get.", "Samuel Goldwyn"),
            new Quote("It does not matter how slowly you go as long as you do not stop.", "Confucius"),
            new Quote("Dost thou love life? Then do not squander time, for that is the stuff life is made of.", "Benjamin Franklin"),
            new Quote("Plans are only good intentions unless they immediately degenerate into hard work.", "Peter Drucker"),
            new Quote("Fall seven times, stand up eight.", "Japanese proverb"),
            new Quote("Focus on being productive instead of busy.", "Tim Ferriss"),
            new Quote("Time is what we want most, but what we use worst.", "William Penn"),
            new Quote("Someday is not a day of the week.", "Janet Dailey"),
            new Quote("Don't count the days, make the days count.", "Muhammad Ali"),
            new Quote("Start where you are. Use what you have. Do what you can.", "Arthur Ashe"),
            new Quote("There is no substitute for hard work.", "Thomas Edison"),
            new Quote("One today is worth two tomorrows.", "Benjamin Franklin"),
            new Quote("Little by little, one travels far.", "Spanish proverb"),
            new Quote("Great acts are made up of small deeds.", "Laozi"),
            new Quote("Success usually comes to those who are too busy to be looking for it.", "Henry David Thoreau"),
            new Quote("Energy and persistence conquer all things.", "Benjamin Franklin"),
            new Quote("If you want to make an easy job seem mighty hard, just keep putting off doing it.", "Olin Miller"),
            new Quote("If you spend too much time thinking about a thing, you'll never get it done.", "Bruce Lee"),
            new Quote("The only place where success comes before work is in the dictionary.", "Vidal Sassoon"),
            new Quote("A year from now you may wish you had started today.", "Karen Lamb"),
            new Quote("Slow and steady wins the race.", "Aesop"),
            new Quote("Diligence is the mother of good luck.", "Benjamin Franklin"),
            new Quote("Tomorrow is often the busiest day of the week.", "Spanish proverb"),
            new Quote("There is no elevator to success; you have to take the stairs.", "Zig Ziglar"),
            new Quote("Without hard work, nothing grows but weeds.", "Gordon B. Hinckley"),
            new Quote("Do something today that your future self will thank you for.", "Sean Patrick Flanery"),
            new Quote("The expert in anything was once a beginner.", "Helen Hayes"),
            new Quote("Persistence guarantees that results are inevitable.", "Paramahansa Yogananda"),
            new Quote("Eighty percent of success is showing up.", "Woody Allen"),
            new Quote("The beginning is the most important part of the work.", "Plato"),
        };

        public static int Count { get { return All.Length; } }

        /// <summary>The next line in the list, moving the saved position on past it. The
        /// position is written straight back to the settings file, so the line after this
        /// one is decided now rather than at the next start - a crash or a kill in between
        /// cannot make the same line come round twice.</summary>
        public static Quote Next()
        {
            int n = All.Length;
            int i = Core.Config.QuoteIndex % n;
            if (i < 0) i += n;
            Core.Config.QuoteIndex = (i + 1) % n;
            Core.Config.Save();
            return All[i];
        }
    }
}
