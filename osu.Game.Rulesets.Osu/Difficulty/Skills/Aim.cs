﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.RootFinding;

using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Osu.Difficulty.Preprocessing;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Difficulty.MathUtil;
using System.Linq;
using System.IO;

namespace osu.Game.Rulesets.Osu.Difficulty.Skills
{
    /// <summary>
    /// Represents the skill required to correctly aim at every object in the map with a uniform CircleSize and normalized distances.
    /// </summary>
    public class Aim : Skill
    {
        private const double probabilityThreshold = 0.02;
        private const double timeThresholdBase = 3600;
        private const double tpMin = 0.1;
        private const double tpMax = 100;
        private const double tpPrecision = 1e-8;

        private const double defaultCheeseLevel = 0.3;
        private const int cheeseLevelCount = 11;

        private const int difficultyCount = 20;


        public static (double, double, double[], double[], double[], double, double[], double[], string)
            CalculateAimAttributes(List<OsuHitObject> hitObjects,
                                   double clockRate,
                                   List<Vector<double>> strainHistory)
        {
            List<OsuMovement> movements = createMovements(hitObjects, clockRate, strainHistory);

            double fcProbTP = calculateFCProbTP(movements);
            double fcTimeTP = calculateFCTimeTP(movements);

            string graphText = generateGraphText(movements, fcProbTP);

            double[] comboTPs = calculateComboTps(movements);
            (var missTPs, var missCounts) = calculateMissTPsMissCounts(movements, fcTimeTP);
            (var cheeseLevels, var cheeseFactors) = calculateCheeseLevelsVSCheeseFactors(movements, fcProbTP);
            double cheeseNoteCount = getCheeseNoteCount(movements, fcProbTP);

            return (fcProbTP, fcTimeTP, comboTPs, missTPs, missCounts, cheeseNoteCount, cheeseLevels, cheeseFactors, graphText);
        }

        private static List<OsuMovement> createMovements(List<OsuHitObject> hitObjects, double clockRate, List<Vector<double>> strainHistory)
        {
            OsuMovement.Initialize();
            var movements = new List<OsuMovement>();

            if (hitObjects.Count == 0)
                return movements;

            // the first object
            movements.AddRange(OsuMovement.ExtractMovement(hitObjects[0]));

            // the rest
            for (int i = 1; i < hitObjects.Count; i++)
            {
                var obj0 = i > 1 ? hitObjects[i - 2] : null;
                var obj1 = hitObjects[i - 1];
                var obj2 = hitObjects[i];
                var obj3 = i < hitObjects.Count - 1 ? hitObjects[i + 1] : null;
                var tapStrain = strainHistory[i];

                movements.AddRange(OsuMovement.ExtractMovement(obj0, obj1, obj2, obj3, tapStrain, clockRate));
            }
            return movements;
        }

        private static double calculateFCProbTP(IEnumerable<OsuMovement> movements, double cheeseLevel = defaultCheeseLevel)
        {
            double fcProbabilityTPMin = calculateFCProb(movements, tpMin, cheeseLevel);

            if (fcProbabilityTPMin >= probabilityThreshold)
                return tpMin;

            double fcProbabilityTPMax = calculateFCProb(movements, tpMax, cheeseLevel);

            if (fcProbabilityTPMax <= probabilityThreshold)
                return tpMax;

            double fcProbMinusThreshold(double tp) => calculateFCProb(movements, tp, cheeseLevel) - probabilityThreshold;
            return Brent.FindRoot(fcProbMinusThreshold, tpMin, tpMax, tpPrecision);
        }

        /// <summary>
        /// Calculates the throughput at which the expected time to FC the given movements =
        /// timeThresholdBase + time span of the movements
        /// </summary>
        private static double calculateFCTimeTP(IEnumerable<OsuMovement> movements)
        {
            if (movements.Count() == 0)
                return 0;

            double mapLength = movements.Last().Time - movements.First().Time;
            double timeThreshold = timeThresholdBase + mapLength;

            double maxFCTime = calculateFCTime(movements, tpMin);

            if (maxFCTime <= timeThreshold)
                return tpMin;

            double minFCTime = calculateFCTime(movements, tpMax);

            if (minFCTime >= timeThreshold)
                return tpMax;

            double fcTimeMinusThreshold(double tp) => calculateFCTime(movements, tp) - timeThreshold;
            return Brent.FindRoot(fcTimeMinusThreshold, tpMin, tpMax, tpPrecision);
        }

        private static string generateGraphText(List<OsuMovement> movements, double tp)
        {
            var sw = new StringWriter();

            foreach (var movement in movements)
            {
                double time = movement.Time;
                double ipRaw = movement.IP12;
                double ipCorrected = FittsLaw.CalculateIP(movement.D, movement.MT * (1 + defaultCheeseLevel * movement.CheesableRatio));
                double missProb = 1 - calculateCheeseHitProb(movement, tp, defaultCheeseLevel);

                sw.WriteLine($"{time} {ipRaw} {ipCorrected} {missProb}");
            }

            string graphText = sw.ToString();
            sw.Dispose();
            return graphText;
        }



        /// <summary>
        /// Calculate miss count for a list of throughputs (used to evaluate miss count of plays).
        /// </summary>
        private static (double[], double[]) calculateMissTPsMissCounts(IList<OsuMovement> movements, double fcTimeTP)
        {
            double[] missTPs = new double[difficultyCount];
            double[] missCounts = new double[difficultyCount];
            double fcProb = calculateFCProb(movements, fcTimeTP, defaultCheeseLevel);

            for (int i = 0; i < difficultyCount; i++)
            {
                double missTP = fcTimeTP * (1 - Math.Pow(i, 1.5) * 0.005);
                double[] missProbs = getMissProbs(movements, missTP);
                missTPs[i] = missTP;
                missCounts[i] = getMissCount(fcProb, missProbs);
            }
            return (missTPs, missCounts);
        }


        /// <summary>
        /// Calculate the probability of missing each note given a skill level.
        /// </summary>
        private static double[] getMissProbs(IList<OsuMovement> movements, double tp)
        {
            // slider breaks should be a miss :( -- joz, 2019
            var missProbs = new double[movements.Count];

            for (int i = 0; i < movements.Count; ++i)
            {
                var movement = movements[i];
                missProbs[i] = 1 - calculateCheeseHitProb(movement, tp, defaultCheeseLevel);
            }

            return missProbs;
        }

        /// <summary>
        /// Find first miss count achievable with at least probability p
        /// </summary>
        private static double getMissCount(double p, double[] missProbabilities)
        {
            var distribution = new PoissonBinomial(missProbabilities);

            Func<double, double> cdfMinusProb = missCount => distribution.Cdf(missCount) - p;
            return Brent.FindRootExpand(cdfMinusProb, -100, 1000);
        }

        private static (double[], double[]) calculateCheeseLevelsVSCheeseFactors(IList<OsuMovement> movements, double fcProbTP)
        {
            double[] cheeseLevels = new double[cheeseLevelCount];
            double[] cheeseFactors = new double[cheeseLevelCount];

            for (int i = 0; i < cheeseLevelCount; i++)
            {
                double cheeseLevel = (double)i / (cheeseLevelCount - 1);
                cheeseLevels[i] = cheeseLevel;
                cheeseFactors[i] = calculateFCProbTP(movements, cheeseLevel) / fcProbTP;
            }
            return (cheeseLevels, cheeseFactors);
        }

        private static double getCheeseNoteCount(IList<OsuMovement> movements, double tp)
        {
            double count = 0;
            foreach (var movement in movements)
            {
                double cheeseness = SpecialFunctions.Logistic((movement.IP12 / tp - 0.6) * 15) * movement.Cheesablility;
                count += cheeseness;
            }

            return count;
        }

        private static double[] calculateComboTps(List<OsuMovement> movements)
        {
            double[] ComboTPs = new double[difficultyCount];

            for (int i = 1; i <= difficultyCount; ++i)
            {
                ComboTPs[i - 1] = double.PositiveInfinity;

                for (int j = 0; j <= difficultyCount - i; ++j)
                {
                    ComboTPs[i - 1] = Math.Min(ComboTPs[i - 1], calculateComboTPForPart(movements, i, j));
                }
            }

            return ComboTPs;
        }

        private static double calculateComboTPForPart(List<OsuMovement> movements, int i, int j)
        {
            int start = movements.Count * j / difficultyCount;
            int end = movements.Count * (j + i) / difficultyCount - 1;

            double partTP = calculateFCTimeTP(movements.GetRange(start, end - start + 1));
            //Console.WriteLine($"{start} {end} {partTP.ToString("N3")}");

            return partTP;
        }

        private static double calculateFCProb(IEnumerable<OsuMovement> movements, double tp, double cheeseLevel)
        {
            double fcProb = 1;

            foreach (OsuMovement movement in movements)
            {
                double hitProb = calculateCheeseHitProb(movement, tp, cheeseLevel);
                fcProb *= hitProb;
            }
            return fcProb;
        }


        // Uses dynamic programming to calculate expected fc time
        private static double calculateFCTime(IEnumerable<OsuMovement> movements, double tp,
                                              double cheeseLevel = defaultCheeseLevel)
        {
            double fcTime = 5;

            foreach (OsuMovement movement in movements)
            {
                double hitProb = calculateCheeseHitProb(movement, tp, cheeseLevel);
                fcTime = (fcTime + movement.RawMT) / (hitProb + 1e-10);
            }
            return fcTime;
        }

        private static double calculateCheeseHitProb(OsuMovement movement, double tp, double cheeseLevel)
        {
            double cheeseMT = movement.MT * (1 + cheeseLevel * movement.CheesableRatio);
            return FittsLaw.CalculateHitProb(movement.D, cheeseMT, tp);
        }

        protected override double SkillMultiplier => throw new NotImplementedException();
        protected override double StrainDecayBase => throw new NotImplementedException();
        protected override double StrainValueOf(DifficultyHitObject current)
        {
            throw new NotImplementedException();
        }
    }
}
