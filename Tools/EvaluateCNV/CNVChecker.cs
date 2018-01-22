﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using CanvasCommon;
using Illumina.Common;
using Isas.SequencingFiles;
using Isas.SequencingFiles.Vcf;
using Illumina.Common.FileSystem;

namespace EvaluateCNV
{

    class CNInterval
    {
        public string Chromosome { get; }
        public int Start; // 0-based inclusive
        public int End; // 0-based exclusive
        public int Cn;
        public int ReferenceCopyNumber = 2; // updated based on ploidy bed
        public int BasesCovered;
        public int BasesExcluded;
        public int BasesCalledCorrectly;

        public int BasesNotCalled => Length - BasesExcluded - BasesCalledCorrectly - BasesCalledIncorrectly;

        public int Length
        {
            get { return End - Start; }
        }

        public int BasesCalledIncorrectly;

        public override string ToString()
        {
            return $"{Chromosome}:{Start + 1}-{End}";
        }

        public CNInterval(string chromosome)
        {
            Chromosome = chromosome;
        }

        public void InitializeInterval()
        {
            BasesCovered = 0;
            BasesExcluded = 0;
            BasesCalledCorrectly = 0;
            BasesCalledIncorrectly = 0;
        }
    }

    class CnvCall
    {
        public int Length => End - Start;

        public string Chr { get; }

        public int Start { get; }

        public int End { get; }

        public int CN { get; }

        public int RefPloidy { get; }

        public string AltAllele { get; }

        public CnvCall(string chr, int start, int end, int cn, int refPloidy, string altAllele)
        {
            Chr = chr;
            Start = start;
            End = end;
            CN = cn;
            RefPloidy = refPloidy;
            AltAllele = altAllele;
        }

        public bool IsAltVariant => CN != RefPloidy;

        public int Overlap(PloidyInterval ploidyInterval)
        {
            int overlapStart = Math.Max(Start, ploidyInterval.Start);
            int overlapEnd = Math.Min(End, ploidyInterval.End);
            if (overlapStart >= overlapEnd) return 0;
            return overlapEnd - overlapStart;
        }
    }

    class CNVChecker
    {
        #region Members
        public Dictionary<string, List<CNInterval>> KnownCn = null;
        public Dictionary<string, List<CNInterval>> RegionsOfInterest = null;
        public Dictionary<string, List<CNInterval>> ExcludeIntervals = null;
        private readonly CnvEvaluator _cnvEvaluator;
        public double? DQscoreThreshold { get; }

        public CNVChecker(double? dQscoreThreshold)
        {
            DQscoreThreshold = dQscoreThreshold;
            _cnvEvaluator = new CnvEvaluator(this);
        }
        #endregion


        /// <summary>
        /// Load known CN data from a .bed file.  File lines have fields:
        /// chromosome, start, end, chromcountA, chromcountB
        /// So, copy number is the sum of the last 2 fields, major chromosome count is the max of the last 2 fields.
        /// </summary>
        /// <param name="oracleBedPath"></param>
        /// <param name="getCn"></param>
        /// <param name="heterogeneityFraction"></param>
        protected Dictionary<string, List<CNInterval>> LoadIntervalsFromBed(string oracleBedPath, bool getCn, double heterogeneityFraction)
        {
            bool stripChr = false;
            int count = 0;
            long totalBases = 0;
            Dictionary<string, List<CNInterval>> bedIntervals = new Dictionary<string, List<CNInterval>>();
            using (FileStream stream = new FileStream(oracleBedPath, FileMode.Open, FileAccess.Read))
            using (StreamReader reader = new StreamReader(stream))
            {
                while (true)
                {
                    string fileLine = reader.ReadLine();
                    if (fileLine == null) break;
                    if (fileLine.Length == 0 || fileLine[0] == '#') continue;
                    string[] bits = fileLine.TrimEnd('\t').Split('\t');
                    if (bits.Length < 3) continue;
                    string chromosome = bits[0];
                    if (stripChr) chromosome = chromosome.Replace("chr", "");
                    if (!bedIntervals.ContainsKey(chromosome)) bedIntervals[chromosome] = new List<CNInterval>();
                    CNInterval interval = new CNInterval(chromosome);
                    interval.Start = int.Parse(bits[1]);
                    interval.End = int.Parse(bits[2]);
                    if (getCn) // bits.Length >= 5)
                    {
                        if (heterogeneityFraction < 1 && bits.Length > 5 && int.Parse(bits[3]) == 1 && int.Parse(bits[4]) == 1)
                            if (heterogeneityFraction > double.Parse(bits[5]))
                                continue;
                        interval.Cn = int.Parse(bits[3]) + int.Parse(bits[4]);
                    }
                    totalBases += interval.Length;
                    bedIntervals[chromosome].Add(interval);
                    count++;
                }
            }
            Console.WriteLine(">>>Loaded {0} CN intervals ({1} bases)", count, totalBases);
            return bedIntervals;
        }


        protected void LoadKnownCNVCF(string oracleVcfPath)
        {
            bool stripChr = false;

            // Load our "oracle" of known copy numbers:
            this.KnownCn = new Dictionary<string, List<CNInterval>>();
            int count = 0;
            using (GzipReader reader = new GzipReader(oracleVcfPath))
            {
                while (true)
                {
                    string fileLine = reader.ReadLine();
                    if (fileLine == null) break;
                    if (fileLine.Length == 0 || fileLine[0] == '#') continue;
                    string[] bits = fileLine.Split('\t');
                    string chromosome = bits[0];
                    if (stripChr) chromosome = chromosome.Replace("chr", "");
                    if (!KnownCn.ContainsKey(chromosome)) KnownCn[chromosome] = new List<CNInterval>();
                    CNInterval interval = new CNInterval(chromosome);
                    interval.Start = int.Parse(bits[1]);
                    interval.Cn = -1;
                    string[] infoBits = bits[7].Split(';');
                    foreach (string subBit in infoBits)
                    {
                        if (subBit.StartsWith("CN="))
                        {
                            float tempCn = float.Parse(subBit.Substring(3));
                            if (subBit.EndsWith(".5"))
                            {
                                interval.Cn = (int)Math.Round(tempCn + 0.1); // round X.5 up to X+1
                            }
                            else
                            {
                                interval.Cn = (int)Math.Round(tempCn); // Round off
                            }
                        }
                        if (subBit.StartsWith("END="))
                        {
                            interval.End = int.Parse(subBit.Substring(4));
                        }
                    }
                    // Parse CN from Canvas output:
                    if (bits.Length > 8)
                    {
                        string[] subBits = bits[8].Split(':');
                        string[] subBits2 = bits[9].Split(':');
                        for (int subBitIndex = 0; subBitIndex < subBits.Length; subBitIndex++)
                        {
                            if (subBits[subBitIndex] == "CN")
                            {
                                interval.Cn = int.Parse(subBits2[subBitIndex]);
                            }
                        }
                    }
                    if (interval.End == 0 || interval.Cn < 0)
                    {
                        Console.WriteLine("Error - bogus record!");
                        Console.WriteLine(fileLine);
                    }
                    else
                    {
                        KnownCn[chromosome].Add(interval);
                        count++;
                    }
                }
            }
            Console.WriteLine(">>>Loaded {0} known-CN intervals", count);
        }

        protected void LoadRegionsOfInterest(string bedPath)
        {
            if (string.IsNullOrEmpty(bedPath)) return;
            if (!File.Exists(bedPath))
            {
                throw new ArgumentException(string.Format("* Error: ROI bed file not found at '{0}'", bedPath));
            }
            this.RegionsOfInterest = this.LoadIntervalsFromBed(bedPath, false, 1.0);
            List<string> keys = this.RegionsOfInterest.Keys.ToList();
            foreach (string key in keys)
            {
                this.RegionsOfInterest[string.Format("chr{0}", key)] = this.RegionsOfInterest[key];
            }
        }

        protected void LoadKnownCn(string oraclePath, double heterogeneityFraction)
        {
            if (!File.Exists(oraclePath))
            {
                throw new ArgumentException(string.Format("* Error: Truth vcf not found at '{0}'", oraclePath));
            }

            if (oraclePath.EndsWith(".bed"))
            {
                this.KnownCn = this.LoadIntervalsFromBed(oraclePath, true, heterogeneityFraction);
                return;
            }
            LoadKnownCNVCF(oraclePath);
            SummarizeTruthSetStatistics();
        }

        public void InitializeIntervalMetrics()
        {
            foreach (var chromosomeIntervals in this.KnownCn.Values)
                foreach (var interval in chromosomeIntervals)
                    interval.InitializeInterval();
        }


        protected void SummarizeTruthSetStatistics()
        {
            List<long> eventSizes = new List<long>();
            double meanEventSize = 0;
            int countUnder10KB = 0;
            int count10kb50kb = 0;
            int count50kb500kb = 0;
            int count500kbplus = 0;
            foreach (string key in KnownCn.Keys)
            {
                foreach (CNInterval interval in KnownCn[key])
                {
                    if (interval.Cn == 2) continue;
                    long length = interval.Length;
                    eventSizes.Add(length);
                    meanEventSize += length;
                    if (length <= 10000)
                    {
                        countUnder10KB++;
                    }
                    else if (length <= 50000)
                    {
                        count10kb50kb++;
                    }
                    else if (length <= 500000)
                    {
                        count50kb500kb++;
                    }
                    else
                    {
                        count500kbplus++;
                    }
                }
            }
            eventSizes.Sort();
            meanEventSize /= eventSizes.Count;
            Console.WriteLine("Known CN: Mean length {0:F4}", meanEventSize);
            Console.WriteLine("up to 10kb: {0}", countUnder10KB);
            Console.WriteLine("10kb-50kb: {0}", count10kb50kb);
            Console.WriteLine("50kb-500kb: {0}", count50kb500kb);
            Console.WriteLine("500kb+: {0}", count500kbplus);
            if (eventSizes.Count > 0)
                Console.WriteLine("Median size: {0}", eventSizes[eventSizes.Count / 2]);
        }

        protected int GetCopyNumber(VcfVariant variant, out int end)
        {
            int CN = -1;
            end = -1;
            if (variant.GenotypeColumns != null && variant.GenotypeColumns.Count > 0)
            {
                Dictionary<string, string> genotype = variant.GenotypeColumns[variant.GenotypeColumns.Count - 1];
                if (genotype.ContainsKey("CN"))
                {
                    CN = int.Parse(genotype["CN"]);
                }
                if (genotype.ContainsKey("END"))
                {
                    end = int.Parse(genotype["END"]);
                }
            }
            if (variant.InfoFields.ContainsKey("END"))
            {
                end = int.Parse(variant.InfoFields["END"]);
            }
            if (variant.InfoFields.ContainsKey("CN"))
            {
                CN = int.Parse(variant.InfoFields["CN"]);
            }

            return CN;
        }

        protected int GetRefPloidy(VcfVariant variant)
        {
            var genotype = variant.GenotypeColumns[variant.GenotypeColumns.Count - 1];
            if (genotype.ContainsKey("GT"))
            {
                var splitReGT = genotype["GT"].Split('/', '|');
                switch (splitReGT.Length)
                {
                    case 1:
                        return 1;
                    case 2:
                        return 2;
                    default:
                        Console.WriteLine($"Warning: variant {variant.Identifier} does not contain GT flag. Using REF ploidy of 2 by default.");
                        return 2;
                }
            }
            else Console.WriteLine($"Warning: could not parse ploidy for {variant.Identifier}. Using REF ploidy of 2 by default.");
            return 2;
        }


        public void CountExcludedBasesInTruthSetIntervals()
        {
            foreach (string key in KnownCn.Keys)
            {
                if (!ExcludeIntervals.ContainsKey(key)) continue;
                foreach (CNInterval interval in KnownCn[key])
                {
                    foreach (CNInterval excludeInterval in ExcludeIntervals[key])
                    {
                        int overlapStart = Math.Max(interval.Start, excludeInterval.Start);
                        int overlapEnd = Math.Min(interval.End, excludeInterval.End);
                        if (overlapStart >= overlapEnd) continue;
                        interval.BasesExcluded += overlapEnd - overlapStart;
                        //Console.WriteLine("Interval {0}:{1}-{2} excludes {3} bases due to overlap with excluded interval {4}:{5}-{6}",
                        //    key, interval.Start, interval.End, overlapEnd - overlapStart,
                        //    key, excludeInterval.Start, excludeInterval.End);
                    }
                }
            }
        }

        private static List<string> GetVcfHeaderLines(IFileLocation vcfPath)
        {
            using (var reader = new VcfReader(vcfPath.FullName, false))
            {
                return reader.HeaderLines;
            }
        }

        private static void HandleHeaderLine(TextWriter writer, List<string> headerLines, string headerKey,
            Action<TextWriter, string> processValue)
        {
            if (headerLines.Any(stringToCheck => stringToCheck.Contains(headerKey)))
                processValue(writer, headerLines.Find(stringToCheck => stringToCheck.Contains(headerKey)));
        }

        private static void LogPurity(TextWriter writer, string value)
        {
            double purity = double.Parse(value.Split("=")[1]);
            writer.WriteLine($"Purity\t{purity}");
        }

        private static void LogPloidy(TextWriter writer, string value)
        {
            double ploidy = double.Parse(value.Split("=")[1]);
            writer.WriteLine($"Ploidy\t{ploidy}");
        }

        public void HandleVcfHeaderInfo(TextWriter outputWriter, IFileLocation vcfPath)
        {
            var headerLines = GetVcfHeaderLines(vcfPath);
            HandleHeaderLine(outputWriter, headerLines, "EstimatedTumorPurity", LogPurity);
            HandleHeaderLine(outputWriter, headerLines, "OverallPloidy", LogPloidy);
        }

        public IEnumerable<CnvCall> GetCnvCallsFromVcf(string vcfPath, bool includePassingOnly)
        {
            using (VcfReader reader = new VcfReader(vcfPath, false))
            {
                if (DQscoreThreshold.HasValue)
                {
                    var match = reader.HeaderLines.FirstOrDefault(stringToCheck => stringToCheck.Contains("DQ"));
                    if (match == null)
                        throw new ArgumentException($"File {vcfPath} does not contain DQ INFO field.");
                }

                foreach (VcfVariant variant in reader.GetVariants())
                {

                    int end;
                    int cn = GetCopyNumber(variant, out end);
                    int refPloidy = GetRefPloidy(variant);
                    if (includePassingOnly && variant.Filters != "PASS") continue;
                    if (DQscoreThreshold.HasValue)
                    {
                        var genotypeColumn = variant.GenotypeColumns.Single();
                        if (!genotypeColumn.Keys.Contains("DQ"))
                            continue;
                        if (variant.Identifier.Contains("REF"))
                            continue;
                        if (genotypeColumn["DQ"] == ".")
                            continue;
                        if (Double.Parse(genotypeColumn["DQ"]) < DQscoreThreshold.Value)
                            continue;
                    }
                    yield return new CnvCall(variant.ReferenceName, variant.ReferencePosition, end, cn, refPloidy, variant.VariantAlleles.First());
                }
            }
        }

        public IEnumerable<CnvCall> GetCnvCallsFromBed(string bedPath, int[] cnIndices = null)
        {
            if (cnIndices == null) { cnIndices = new[] { 3 }; }
            int maxCnIndex = cnIndices.Max();
            using (FileStream stream = new FileStream(bedPath, FileMode.Open, FileAccess.Read))
            using (StreamReader reader = new StreamReader(stream))
            {
                string line;
                string[] toks;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("#")) { continue; } // skip comments
                    toks = line.Split('\t');
                    if (toks.Length <= maxCnIndex)
                    {
                        Console.WriteLine("Error: Line has fewer than {0} columns: {1}", maxCnIndex + 1, line);
                        continue;
                    }
                    string chr;
                    int start;
                    int end;
                    int cn;
                    try
                    {
                        chr = toks[0];
                        start = int.Parse(toks[1]);
                        end = int.Parse(toks[2]);
                        cn = cnIndices.Sum(cnIndex => int.Parse(toks[cnIndex]));
                    }
                    catch
                    {
                        Console.WriteLine("Error: Failed to parse line: {0}", line);
                        continue;
                    }
                    yield return new CnvCall(chr, start, end, cn, 2, null);
                }
            }
        }

        protected void ComputeAccuracy(string truthSetPath, string cnvCallsPath, string outputPath, PloidyInfo ploidyInfo,
            bool includePassingOnly, EvaluateCnvOptions options)
        {
            _cnvEvaluator.ComputeAccuracy(truthSetPath, cnvCallsPath, outputPath, ploidyInfo, includePassingOnly, options);
            if (includePassingOnly)
                _cnvEvaluator.ComputeAccuracy(truthSetPath, cnvCallsPath, outputPath, ploidyInfo, false, options);
        }

        public void Evaluate(string truthSetPath, string cnvCallsPath, string excludedBed, string outputPath, EvaluateCnvOptions options)
        {
            double heterogeneityFraction = options.HeterogeneityFraction;
            var cnvCallsFile = new FileLocation(cnvCallsPath);
            var ploidyInfo = LoadPloidy(options.PloidyFile, cnvCallsFile);

            LoadKnownCn(truthSetPath, heterogeneityFraction);
            ploidyInfo.MakeChromsomeNameAgnosticWithAllChromosomes(KnownCn.Keys);
            SetTruthsetReferencePloidy(ploidyInfo);

            // LoadRegionsOfInterest(options.RoiBed?.FullName);
            if (!string.IsNullOrEmpty(excludedBed))
            {
                ExcludeIntervals = LoadIntervalsFromBed(excludedBed, false, 1.0);
                // cheesy logic to handle different chromosome names:
                List<string> keys = this.ExcludeIntervals.Keys.ToList();
                foreach (string key in keys)
                {
                    ExcludeIntervals[key.Replace("chr", "")] = ExcludeIntervals[key];
                }
            }
            Console.WriteLine("TruthSet\t{0}", truthSetPath);
            Console.WriteLine("CNVCalls\t{0}", cnvCallsPath);

            var includePassingOnly = Path.GetFileName(cnvCallsPath).ToLower().Contains("vcf");
            ComputeAccuracy(truthSetPath, cnvCallsPath, outputPath, ploidyInfo, includePassingOnly, options);

            Console.WriteLine(">>>Done - results written to {0}", outputPath);
        }

        private static PloidyInfo LoadPloidy(IFileLocation ploidyFile, IFileLocation cnvCalls)
        {
            if (ploidyFile == null) return new PloidyInfo();
            if (!ploidyFile.FullName.EndsWith(".vcf") && !ploidyFile.FullName.EndsWith(".vcf.gz"))
            {
                throw new NotSupportedException("Ploidy information must be provided in VCF format.");
            }
            var sampleId = GetSampleIdFromVcfHeader(cnvCalls);
            return PloidyInfo.LoadPloidyFromVcfFile(ploidyFile.FullName, sampleId);
        }

        private static string GetSampleIdFromVcfHeader(IFileLocation cnvCallsPath)
        {
            using (var reader = new VcfReader(cnvCallsPath.FullName))
            {
                return reader.Samples.Single();
            }
        }

        private void SetTruthsetReferencePloidy(PloidyInfo ploidyInfo)
        {
            foreach (string chromosome in KnownCn.Keys)
            {
                foreach (CNInterval truthInterval in KnownCn[chromosome])
                {
                    foreach (PloidyInterval ploidyRegion in ploidyInfo.PloidyByChromosome[chromosome])
                    {
                        // truth interval must be completely contained within the ploidy region
                        if (truthInterval.End >= ploidyRegion.Start && truthInterval.Start <= ploidyRegion.End)
                        {
                            truthInterval.ReferenceCopyNumber = ploidyRegion.Ploidy;
                            break;
                        }
                        if (truthInterval.Start >= ploidyRegion.Start && truthInterval.Start <= ploidyRegion.End ||
                            truthInterval.End >= ploidyRegion.Start && truthInterval.End <= ploidyRegion.End)
                            throw new Illumina.Common.IlluminaException($"Truth interval {truthInterval} crosses reference ploidy region {ploidyRegion}. Update truth interval");
                    }
                }
            }
        }
    }
}
