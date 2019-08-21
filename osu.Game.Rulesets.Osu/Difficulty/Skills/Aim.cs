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

namespace osu.Game.Rulesets.Osu.Difficulty.Skills
{
    /// <summary>
    /// Represents the skill required to correctly aim at every object in the map with a uniform CircleSize and normalized distances.
    /// </summary>
    public class Aim : Skill
    {
        private const double probabilityThreshold = 0.02;
        private const double timeThreshold = 3600;
        private const double tpMin = 0.1;
        private const double tpMax = 100;
        private const double tpPrecision = 1e-8;

        public static List<OsuMovement> CreateMovements(List<OsuHitObject> hitObjects, double clockRate, List<Vector<double>> strainHistory)
        {
            OsuMovement.Initialize();
            var movements = new List<OsuMovement>();

            for (int i = 1; i < hitObjects.Count; i++)
            {
                var obj0 = i > 1 ? hitObjects[i - 2] : null;
                var obj1 = hitObjects[i - 1];
                var obj2 = hitObjects[i];
                var obj3 = i < hitObjects.Count - 1 ? hitObjects[i + 1] : null;
                var tapStrain = strainHistory[i];

                movements.Add(new OsuMovement(obj0, obj1, obj2, obj3, tapStrain, clockRate));
            }

            return movements;
        }

        public static double CalculateFCProbTP(IEnumerable<OsuMovement> movements)
        {
            double fcProbabilityTPMin = calculateFCProb(movements, tpMin);

            if (fcProbabilityTPMin >= probabilityThreshold)
                return tpMin;

            double fcProbabilityTPMax = calculateFCProb(movements, tpMax);

            if (fcProbabilityTPMax <= probabilityThreshold)
                return tpMax;

            Func<double, double> fcProbMinusThreshold = tp => calculateFCProb(movements, tp) - probabilityThreshold;
            return Brent.FindRoot(fcProbMinusThreshold, tpMin, tpMax, tpPrecision);
        }

        public static double CalculateFCTimeTP(IEnumerable<OsuMovement> movements, double mapLength)
        {
            
            double maxFCTime = calculateFCTime(movements, mapLength, tpMin);

            if (maxFCTime <= timeThreshold)
                return tpMin;

            double minFCTime = calculateFCTime(movements, mapLength, tpMax);

            if (minFCTime >= timeThreshold)
                return tpMax;

            Func<double, double> fcTimeMinusThreshold = tp => calculateFCTime(movements, mapLength, tp) - timeThreshold;
            return Brent.FindRoot(fcTimeMinusThreshold, tpMin, tpMax, tpPrecision);

        }

        private static double calculateFCProb(IEnumerable<OsuMovement> movements, double tp)
        {
            double fcProb = 1;

            foreach (OsuMovement movement in movements)
            {
                double hitProb = FittsLaw.CalculateHitProb(movement.D, movement.MT, tp);
                fcProb *= hitProb;
            }
            return fcProb;
        }

        private static double calculateFCTime(IEnumerable<OsuMovement> movements, double mapLength, double tp)
        {
            double fcTime = 0;

            foreach (OsuMovement movement in movements)
            {
                double hitProb = FittsLaw.CalculateHitProb(movement.D, movement.MT, tp);
                fcTime = (fcTime + movement.RawMT) / hitProb;
            }

            return fcTime;
        }



        protected override double SkillMultiplier => throw new NotImplementedException();
        protected override double StrainDecayBase => throw new NotImplementedException();
        protected override double StrainValueOf(DifficultyHitObject current)
        {
            throw new NotImplementedException();
        }
    }
}
