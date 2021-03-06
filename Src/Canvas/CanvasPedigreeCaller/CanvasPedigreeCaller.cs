﻿using CanvasCommon;
using Combinatorics.Collections;
using Illumina.Common;
using Illumina.Common.FileSystem;
using Isas.Framework.DataTypes;
using Isas.Framework.Logging;
using Isas.SequencingFiles;
using Isas.SequencingFiles.Vcf;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CanvasCommon.Visualization;
using CanvasPedigreeCaller.Visualization;
using Isas.Framework.DataTypes.Maps;
using Genotype = CanvasCommon.Genotype;

namespace CanvasPedigreeCaller
{
    class CanvasPedigreeCaller
    {
        #region Members
        public const int DefaultQualityFilterThreshold = 7;
        public const int DefaultDeNovoQualityFilterThreshold = 20;
        private readonly int _qualityFilterThreshold;
        private readonly int _deNovoQualityFilterThreshold;
        private readonly PedigreeCallerParameters _callerParameters;
        private readonly ILogger _logger;
        private readonly IVariantCaller _variantCaller;
        private readonly CopyNumberLikelihoodCalculator _copyNumberLikelihoodCalculator;
        private readonly ICoverageBigWigWriter _coverageBigWigWriter;
        private readonly ICopyNumberModelFactory _copyNumberModelFactory;
        private readonly ICopyNumberBedGraphWriter _copyNumberBedGraphWriter;
        private readonly CoverageBedGraphWriter _partitionCoverageBedGraphWriter;

        #endregion

        public CanvasPedigreeCaller(ILogger logger, int qualityFilterThreshold, int deNovoQualityFilterThreshold,
            PedigreeCallerParameters callerParameters, CopyNumberLikelihoodCalculator copyNumberLikelihoodCalculator,
            IVariantCaller variantCaller, ICoverageBigWigWriter coverageBigWigWriter,
            ICopyNumberModelFactory copyNumberModelFactory, ICopyNumberBedGraphWriter copyNumberBedGraphWriter,
            CoverageBedGraphWriter partitionCoverageBedGraphWriter)
        {
            _logger = logger;
            _qualityFilterThreshold = qualityFilterThreshold;
            _deNovoQualityFilterThreshold = deNovoQualityFilterThreshold;
            _callerParameters = callerParameters;
            _copyNumberLikelihoodCalculator = copyNumberLikelihoodCalculator;
            _variantCaller = variantCaller;
            _coverageBigWigWriter = coverageBigWigWriter;
            _copyNumberModelFactory = copyNumberModelFactory;
            _copyNumberBedGraphWriter = copyNumberBedGraphWriter;
            _partitionCoverageBedGraphWriter = partitionCoverageBedGraphWriter;
        }

        /// <summary>
        /// For each segment shorter than 10kb, flag it as filtered.
        /// </summary>
        private void FilterExcessivelyShortSegments(ISampleMap<List<CanvasSegment>> segments)
        {
            string sizeFilter = CanvasFilter.GetCnvSizeFilter(CanvasFilter.SegmentSizeCutoff);

            foreach (var segmentList in segments.Values)
            {
                foreach (var segment in segmentList)
                {
                    if (segment.Length >= CanvasFilter.SegmentSizeCutoff) continue;
                    segment.Filter = segment.Filter.AddFilter(sizeFilter);
                }
            }
        }

        internal int CallVariants(List<string> variantFrequencyFiles, List<string> segmentFiles,
            IFileLocation outVcfFile, string ploidyBedPath, string referenceFolder, List<string> sampleNames, string commonCnvsBedPath, List<SampleType> sampleTypes)
        {
            // load files
            // initialize data structures and classes
            var fileCounter = 0;
            var samplesInfo = new SampleMap<SampleMetrics>();
            var sampleSegments = new SampleMap<Segments>();
            var copyNumberModels = new SampleMap<ICopyNumberModel>();
            var variantFrequencyFilesSampleList = new SampleMap<string>();
            var kinships = new SampleMap<SampleType>();

            foreach (string sampleName in sampleNames)
            {
                var sampleId = new SampleId(sampleName);
                var segment = Segments.ReadSegments(_logger, new FileLocation(segmentFiles[fileCounter]));
                segment.AddAlleles(CanvasIO.ReadFrequenciesWrapper(_logger, new FileLocation(variantFrequencyFiles[fileCounter]), segment.IntervalsByChromosome));
                sampleSegments.Add(sampleId, segment);
                var sampleInfo = SampleMetrics.GetSampleInfo(segment.AllSegments, ploidyBedPath, _callerParameters.NumberOfTrimmedBins, sampleId);
                var copyNumberModel = _copyNumberModelFactory.CreateModel(_callerParameters.MaximumCopyNumber, sampleInfo.MaxCoverage, sampleInfo.MeanCoverage, sampleInfo.MeanMafCoverage);
                samplesInfo.Add(sampleId, sampleInfo);
                copyNumberModels.Add(sampleId, copyNumberModel);
                variantFrequencyFilesSampleList.Add(sampleId, variantFrequencyFiles[fileCounter]);
                kinships.Add(sampleId, sampleTypes[fileCounter]);
                fileCounter++;
            }
            var segmentSetsFromCommonCnvs = CreateSegmentSetsFromCommonCnvs(variantFrequencyFilesSampleList,
                _callerParameters.MinAlleleCountsThreshold, commonCnvsBedPath, sampleSegments);

            var segmentsForVariantCalling = GetHighestLikelihoodSegments(segmentSetsFromCommonCnvs, samplesInfo, copyNumberModels).ToList();
            PedigreeInfo pedigreeInfo = PedigreeInfo.GetPedigreeInfo(kinships, _callerParameters);
            Parallel.ForEach(
                segmentsForVariantCalling,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, _callerParameters.MaxCoreNumber)
                },
                segments => _variantCaller.CallVariant(segments, samplesInfo, copyNumberModels, pedigreeInfo)
            );
            var variantCalledSegments = new SampleMap<List<CanvasSegment>>();
            foreach (var key in samplesInfo.SampleIds)
                variantCalledSegments.Add(key, segmentsForVariantCalling.Select(segment => segment[key]).ToList());

            var mergedVariantCalledSegments = MergeSegments(variantCalledSegments, _callerParameters.MinimumCallSize, _qualityFilterThreshold);

            FilterExcessivelyShortSegments(mergedVariantCalledSegments);

            var outputFolder = outVcfFile.Directory;
            foreach (var sampleId in samplesInfo.SampleIds)
            {
                var coverageOutputPath = SingleSampleCallset.GetCoverageAndVariantFrequencyOutput(outputFolder,
                    sampleId.ToString());
                CanvasSegment.WriteCoveragePlotData(mergedVariantCalledSegments[sampleId], samplesInfo[sampleId].MeanCoverage,
                    samplesInfo[sampleId].Ploidy, coverageOutputPath, referenceFolder);
            }
            bool isPedigreeInfoSupplied = pedigreeInfo != null && pedigreeInfo.HasFullPedigree();
            var denovoQualityThreshold = isPedigreeInfoSupplied ? (int?)_deNovoQualityFilterThreshold : null;
            var ploidies = samplesInfo.Select(info => info.Value.Ploidy).ToList();
            var diploidCoverage = samplesInfo.Select(info => info.Value.MeanCoverage).ToList();
            var names = samplesInfo.SampleIds.Select(id => id.ToString()).ToList();
            CanvasSegmentWriter.WriteMultiSampleSegments(outVcfFile.FullName, mergedVariantCalledSegments, diploidCoverage, referenceFolder, names,
                null, ploidies, _qualityFilterThreshold, denovoQualityThreshold, CanvasFilter.SegmentSizeCutoff, isPedigreeInfoSupplied);

            foreach (var sampleId in samplesInfo.SampleIds)
            {
                var outputVcfPath = SingleSampleCallset.GetVcfOutput(outputFolder, sampleId.ToString());
                var sampleMetrics = samplesInfo[sampleId];
                var segments = mergedVariantCalledSegments[sampleId];
                CanvasSegmentWriter.WriteSegments(outputVcfPath.FullName, segments,
                    sampleMetrics.MeanCoverage, referenceFolder, sampleId.ToString(), null,
                    sampleMetrics.Ploidy, _qualityFilterThreshold, isPedigreeInfoSupplied, denovoQualityThreshold, null);

                var visualizationTemp = outputFolder.CreateSubdirectory($"VisualizationTemp{sampleId}");
                var normalizationFactor = NormalizationCalculator.ComputeNormalizationFactor(segments);
                var bigWig = _coverageBigWigWriter.Write(segments, visualizationTemp, normalizationFactor);
                bigWig?.MoveTo(SingleSampleCallset.GetCoverageBigWig(outputFolder, sampleId.ToString()));
                var copyNumberBedGraph = SingleSampleCallset.GetCopyNumberBedGraph(outputFolder, sampleId.ToString());
                _copyNumberBedGraphWriter.Write(segments, sampleMetrics.Ploidy, copyNumberBedGraph);

                var partitionBedgraphHeader = "track type=bedGraph visibility=full autoScale=on graphType=points";
                var originalSegments = sampleSegments[sampleId];
                _partitionCoverageBedGraphWriter.Write(originalSegments.AllSegments, SingleSampleCallset.GetPartitionBedGraph(outputFolder, sampleId.ToString()), normalizationFactor, partitionBedgraphHeader);
            }
            return 0;
        }

        private IEnumerable<ISampleMap<CanvasSegment>> GetHighestLikelihoodSegments(IEnumerable<ISampleMap<OverlappingSegmentsRegion>> segmentSetsFromCommonCnvs,
            ISampleMap<SampleMetrics> pedigreeMembersInfo, ISampleMap<ICopyNumberModel> copyNumberModel)
        {
            var updatedSegmentSets = segmentSetsFromCommonCnvs
                .AsParallel()
                .AsOrdered()
                .WithDegreeOfParallelism(Math.Min(Environment.ProcessorCount, _callerParameters.MaxCoreNumber))
                .Select(segmentSet =>
                {
                    GetHighestLogLikelihoodSegmentsSet(segmentSet, pedigreeMembersInfo, copyNumberModel);
                    return segmentSet;
                });

            return updatedSegmentSets
                .SelectMany(sampleMap => sampleMap.SelectValues(x => x.GetSet().AsEnumerable()).Zip())
                .ToList();
        }


        private static ISampleMap<List<CanvasSegment>> MergeSegments(ISampleMap<List<CanvasSegment>> segments, int minimumCallSize, int qScoreThreshold)
        {
            int nSegments = segments.First().Value.Count;
            var copyNumbers = new List<List<int>>(nSegments);
            var qscores = new List<double>(nSegments);
            foreach (int segmentIndex in Enumerable.Range(0, nSegments))
            {
                copyNumbers.Add(segments.Select(s => s.Value[segmentIndex].CopyNumber).ToList());
                qscores.Add(segments.Select(s => s.Value[segmentIndex].QScore).Average());
            }

            if (copyNumbers == null && qscores != null || copyNumbers != null & qscores == null)
                throw new ArgumentException("Both copyNumbers and qscores arguments must be specified.");
            if (copyNumbers != null && copyNumbers.Count != nSegments)
                throw new ArgumentException("Length of copyNumbers list should be equal to the number of segments.");
            if (qscores != null && qscores.Count != nSegments)
                throw new ArgumentException("Length of qscores list should be equal to the number of segments.");

            var mergedSegments = new SampleMap<List<CanvasSegment>>();
            foreach (var sampleSegments in segments)
            {
                var mergedSegmentsThisSample = CanvasSegment.MergeSegments(sampleSegments.Value.ToList(),
                    minimumCallSize, 10000, copyNumbers, qscores, qScoreThreshold);
                mergedSegments.Add(sampleSegments.Key, mergedSegmentsThisSample);
            }
            return mergedSegments;
        }

        /// <summary>
        /// CreatRecordLevelFilter CanvasSegments from common CNVs bed file and overlap with CanvasPartition
        /// segments to create SegmentHaplotypes
        /// </summary>
        private IEnumerable<ISampleMap<OverlappingSegmentsRegion>> CreateSegmentSetsFromCommonCnvs(ISampleMap<string> variantFrequencyFiles,
            int defaultAlleleCountThreshold, string commonCNVsbedPath, ISampleMap<Segments> sampleSegments)
        {
            if (commonCNVsbedPath == null)
            {
                var defaultSampleRegions = sampleSegments
                    .SelectValues(segments => segments.AllSegments.Select(segment => new OverlappingSegmentsRegion(segment)).ToList());
                return GetOverlappingSegmentsRegionSampleLists(defaultSampleRegions);
            }

            var commonRegions = ReadCommonRegions(commonCNVsbedPath);
            var chromosomes = sampleSegments.Values.First().GetChromosomes();
            if (IsIdenticalChromosomeNames(commonRegions, chromosomes))
                throw new ArgumentException(
                    $"Chromosome names in a common CNVs bed file {commonCNVsbedPath} does not match the genome reference");

            var segmentIntervalsByChromosome = new Dictionary<string, List<BedInterval>>();
            var genomicBinsByChromosome = new Dictionary<string, IReadOnlyList<SampleGenomicBin>>();

            Parallel.ForEach(
                chromosomes,
                chr =>
                {
                    genomicBinsByChromosome[chr] = sampleSegments.Values.First().GetGenomicBinsForChromosome(chr);
                    segmentIntervalsByChromosome[chr] =
                        CanvasSegment.RemapGenomicToBinCoordinates(commonRegions[chr], genomicBinsByChromosome[chr]);
                });

            var sampleRegions = new SampleMap<List<OverlappingSegmentsRegion>>();
            foreach (var sampleId in sampleSegments.SampleIds)
            {
                var commonIntervals = commonRegions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Select(bedEntry => bedEntry.Interval).ToList());
                var allelesByChromosomeCommonSegs = CanvasIO.ReadFrequenciesWrapper(_logger,
                    new FileLocation(variantFrequencyFiles[sampleId]), commonIntervals);
                var segmentsSets = GetSegmentSets(defaultAlleleCountThreshold, commonRegions,
                    genomicBinsByChromosome, segmentIntervalsByChromosome, allelesByChromosomeCommonSegs, sampleSegments[sampleId]);
                sampleRegions.Add(sampleId, segmentsSets);
            }

            return GetOverlappingSegmentsRegionSampleLists(sampleRegions);
        }

        private static IEnumerable<ISampleMap<OverlappingSegmentsRegion>> GetOverlappingSegmentsRegionSampleLists(ISampleMap<List<OverlappingSegmentsRegion>> sampleRegions)
        {
            return sampleRegions.Zip();
        }

        private static List<OverlappingSegmentsRegion> GetSegmentSets(int defaultAlleleCountThreshold, Dictionary<string, List<BedEntry>> commonRegions,
            Dictionary<string, IReadOnlyList<SampleGenomicBin>> genomicBinsByChromosome, Dictionary<string, List<BedInterval>> segmentIntervalsByChromosome,
            Dictionary<string, List<Balleles>> allelesByChromosomeCommonSegs, Segments segments)
        {
            var segmentsSetByChromosome = new ConcurrentDictionary<string, List<OverlappingSegmentsRegion>>();
            Parallel.ForEach(
                segments.GetChromosomes(),
                chr =>
                {
                    var segmentsByChromosome = segments.GetSegmentsForChromosome(chr).ToList();

                    if (commonRegions.Keys.Any(chromosome => chromosome == chr))
                    {
                        var commonCnvCanvasSegments = CanvasSegment.CreateSegmentsFromCommonCnvs(genomicBinsByChromosome[chr],
                            segmentIntervalsByChromosome[chr], allelesByChromosomeCommonSegs[chr]);

                        segmentsSetByChromosome[chr] = CanvasSegment.MergeCommonCnvSegments(segmentsByChromosome,
                            commonCnvCanvasSegments, defaultAlleleCountThreshold);
                    }
                    else
                    {
                        segmentsSetByChromosome[chr] = segmentsByChromosome.Select(
                            segment => new OverlappingSegmentsRegion(new List<CanvasSegment> { segment }, null)).ToList();
                    }
                });
            return segmentsSetByChromosome.OrderBy(i => i.Key).Select(x => x.Value).SelectMany(x => x).ToList();
        }

        private static Dictionary<string, List<BedEntry>> ReadCommonRegions(string commonCNVsbedPath)
        {
            Dictionary<string, List<BedEntry>> commonRegions;
            using (var reader = new BedReader(new GzipOrTextReader(commonCNVsbedPath)))
            {
                var commonTmpRegions = reader.LoadAllEntries();
                commonRegions = Utilities.SortAndOverlapCheck(commonTmpRegions, commonCNVsbedPath);
            }
            return commonRegions;
        }

        private static bool IsIdenticalChromosomeNames(Dictionary<string, List<BedEntry>> commonRegions,
            ICollection<string> chromosomeNames)
        {
            var chromosomes = new HashSet<string>(chromosomeNames);
            return commonRegions.Keys.Count(chromosome => chromosomes.Contains(chromosome)) == 0;
        }

        public static int AggregateVariantCoverage(ref List<CanvasSegment> segments)
        {
            var variantCoverage = segments.SelectMany(segment => segment.Balleles.TotalCoverage).ToList();
            return variantCoverage.Any() ? Utilities.Median(variantCoverage) : 0;
        }

        /// <summary>
        /// Identify variant with the highest likelihood at a given setPosition and assign relevant scores
        /// </summary>
        private void GetHighestLogLikelihoodSegmentsSet(ISampleMap<OverlappingSegmentsRegion> canvasSegmentsSet, ISampleMap<SampleMetrics> pedigreeMembersInfo,
            ISampleMap<ICopyNumberModel> model)
        {
            SegmentsSet segmentSet;

            if (canvasSegmentsSet.Values.First().SetA == null)
                segmentSet = SegmentsSet.SetB;
            else if (canvasSegmentsSet.Values.First().SetB == null)
                segmentSet = SegmentsSet.SetA;
            else
                segmentSet = GetSegmentSetLogLikelihood(canvasSegmentsSet, pedigreeMembersInfo, model,
                                 SegmentsSet.SetA) >
                             GetSegmentSetLogLikelihood(canvasSegmentsSet, pedigreeMembersInfo, model,
                                 SegmentsSet.SetB)
                    ? SegmentsSet.SetA
                    : SegmentsSet.SetB;

            canvasSegmentsSet.SampleIds.ForEach(id => canvasSegmentsSet[id].SetSet(segmentSet));
        }

        /// <summary>
        /// Given a set canvasSegmentsSet with two alternative segmentation hypothesis (SegmentsSet: SetA and SetB), return log likelihood 
        /// for a segmentation hypothesis specified by segmentsSet. Segmentation hypothesis could typically include segmentation results specified 
        /// by partitioning or annotations of population (common) variants  
        /// </summary>
        /// <param name="canvasSegmentsSet"></param>
        /// <param name="samplesInfo"></param>
        /// <param name="copyNumberModel"></param>
        /// <param name="segmentsSet"></param>
        /// <returns></returns>
        private double GetSegmentSetLogLikelihood(ISampleMap<OverlappingSegmentsRegion> canvasSegmentsSet, ISampleMap<SampleMetrics> samplesInfo,
            ISampleMap<ICopyNumberModel> copyNumberModel, SegmentsSet segmentsSet)
        {
            double segmentSetLogLikelihood = 0;
            foreach (var sampleId in canvasSegmentsSet.SampleIds)
                canvasSegmentsSet[sampleId].SetSet(segmentsSet);

            var canvasSegments = new List<ISampleMap<CanvasSegment>>();
            int nSegments = canvasSegmentsSet.First().Value.GetSet().Count;
            for (var canvasSegmentIndex = 0; canvasSegmentIndex < nSegments; canvasSegmentIndex++)
            {
                var canvasSegment = new SampleMap<CanvasSegment>();
                foreach (var id in canvasSegmentsSet.SampleIds)
                    canvasSegment.Add(id, canvasSegmentsSet[id].GetSet()[canvasSegmentIndex]);
                canvasSegments.Add(canvasSegment);
            }
            foreach (var canvasSegment in canvasSegments)
            {
                var copyNumbersLikelihoods = _copyNumberLikelihoodCalculator.GetCopyNumbersLikelihoods(canvasSegment, samplesInfo, copyNumberModel);
                var (_, likelihoods) = GetCopyNumbersNoPedigreeInfo(canvasSegment, copyNumbersLikelihoods);
                segmentSetLogLikelihood += likelihoods.MaximalLogLikelihood;
            }

            return segmentSetLogLikelihood;
        }

        /// <summary>
        /// Evaluate joint log likelihood of all genotype combinations across samples. 
        /// Return joint likelihood object and the copy number states with the highest likelihood 
        /// </summary>
        public static (SampleMap<Genotype> copyNumbersGenotypes, JointLikelihoods jointLikelihood) GetCopyNumbersNoPedigreeInfo(ISampleMap<CanvasSegment> segments,
            ISampleMap<Dictionary<Genotype, double>> singleSampleLikelihoods)
        {
            // for non-pedigree samples JointLogLikelihoods object contains only maximum likelihood information
            var jointLogLikelihoods = new JointLikelihoods();
            var sampleCopyNumbersGenotypes = new SampleMap<Genotype>();
            foreach (var sampleId in segments.SampleIds)
            {
                var (copyNumber, maxSampleLikelihood) = singleSampleLikelihoods[sampleId].MaxBy(x => x.Value);
                jointLogLikelihoods.MaximalLogLikelihood += Math.Log(maxSampleLikelihood);
                sampleCopyNumbersGenotypes.Add(sampleId, copyNumber);
            }
            return (copyNumbersGenotypes: sampleCopyNumbersGenotypes, jointLikelihood: jointLogLikelihoods);
        }

        /// <summary>
        /// Generate all possible copy number combinations with the maximal number of copy numbers per segment set to maxAlleleNumber.
        /// </summary>
        /// <param name="numberOfCnStates"></param>
        /// <param name="maxAlleleNumber"></param>
        /// <returns></returns>
        public static List<List<int>> GenerateCopyNumberCombinations(int numberOfCnStates, int maxAlleleNumber)
        {
            if (numberOfCnStates <= 0)
                throw new ArgumentOutOfRangeException(nameof(numberOfCnStates));
            var cnStates = Enumerable.Range(0, numberOfCnStates).ToList();
            var allCombinations = new List<List<int>>();
            for (int currentAlleleNumber = 1; currentAlleleNumber <= maxAlleleNumber; currentAlleleNumber++)
            {
                var currentCombination = new Combinations<int>(cnStates, currentAlleleNumber);
                var list = currentCombination.Select(x => x.ToList()).ToList();
                allCombinations.AddRange(list);
            }
            return allCombinations;
        }

        /// <summary>
        /// Generate all possible copy number genotype combinations with the maximal number of alleles per segment set to maxAlleleNumber.
        /// </summary>
        /// <param name="copyNumber"></param>
        /// <returns> </returns>
        public static List<int> GenerateCnAlleles(int copyNumber)
        {
            if (copyNumber == 0)
                return new List<int> { 0 };

            if (copyNumber == 1)
                return new List<int> { 0, 1 };

            var alleles = new List<int>();
            for (int allele = 1; allele <= copyNumber; allele++)
                alleles.Add(allele);

            return alleles;
        }

        public static SampleMap<Genotype> GetNonPedigreeCopyNumbers(ISampleMap<CanvasSegment> canvasSegments, PedigreeInfo pedigreeInfo,
            ISampleMap<Dictionary<Genotype, double>> singleSampleCopyNumberLogLikelihoods)
        {
            bool IsOther(SampleId sampleId) => pedigreeInfo.OtherIds.Contains(sampleId);
            var nonPedigreeMemberSegments = canvasSegments.WhereSampleIds(IsOther);
            var nonPedigreeMemberLikelihoods = singleSampleCopyNumberLogLikelihoods.WhereSampleIds(IsOther);
            (var nonPedigreeMemberCopyNumbers, _) = GetCopyNumbersNoPedigreeInfo(nonPedigreeMemberSegments, nonPedigreeMemberLikelihoods);
            return nonPedigreeMemberCopyNumbers;
        }


        /// <summary>
        /// Derives metrics from b-allele counts within each segment and determines whereas to use them for calculating MCC
        /// </summary>
        /// <param name="canvasSegments"></param>
        /// <param name="minAlleleCountsThreshold"></param>
        /// <param name="minAlleleNumberInSegment"></param>
        /// <returns></returns>
        public static bool UseAlleleCountsInformation(ISampleMap<CanvasSegment> canvasSegments,
            int minAlleleCountsThreshold, int minAlleleNumberInSegment)
        {
            var alleles = canvasSegments.Values.Select(segment => segment.Balleles?.TotalCoverage);
            // allele read coverage check
            var alleleCounts = alleles.Select(allele => allele?.Where(y => y >= minAlleleCountsThreshold).Count() ?? 0).ToList();
            // number of SNVs in a segment check
            bool sufficientAlleleNum = alleleCounts.All(x => x >= minAlleleNumberInSegment);
            return sufficientAlleleNum;
        }

        /// <summary>
        /// Assess likelihood of a de-novo variant for copyNumberGenotypes configuration with a Mendelian conflict 
        /// </summary>
        /// <param name="canvasSegments"></param>
        /// <param name="jointLikelihoods"></param>
        /// <param name="parentIDs"></param>
        /// <param name="probandId"></param>
        /// <param name="samplesInfo"></param>
        /// <returns></returns>
        internal static double GetConditionalDeNovoQualityScore(ISampleMap<CanvasSegment> canvasSegments, JointLikelihoods jointLikelihoods, ISampleMap<SampleMetrics> samplesInfo, List<SampleId> parentIDs, SampleId probandId)
        {
            const double q60 = 0.000001;
            var parent1Ploidy = Genotype.Create(samplesInfo[parentIDs.First()].GetPloidy(canvasSegments[parentIDs.First()]));
            var parent2Ploidy = Genotype.Create(samplesInfo[parentIDs.Last()].GetPloidy(canvasSegments[parentIDs.Last()]));
            int probandPloidy = samplesInfo[probandId].GetPloidy(canvasSegments[probandId]);

            double deNovoGainMarginalLikelihood = jointLikelihoods.GetMarginalGainDeNovoLikelihood(new KeyValuePair<SampleId, Genotype>(probandId, Genotype.Create(probandPloidy)),
                new KeyValuePair<SampleId, Genotype>(parentIDs.First(), parent1Ploidy), new KeyValuePair<SampleId, Genotype>(parentIDs.Last(), parent2Ploidy));
            double deNovoLossMarginalLikelihood = jointLikelihoods.GetMarginalLossDeNovoLikelihood(new KeyValuePair<SampleId, Genotype>(probandId, Genotype.Create(probandPloidy)),
                new KeyValuePair<SampleId, Genotype>(parentIDs.First(), parent1Ploidy), new KeyValuePair<SampleId, Genotype>(parentIDs.Last(), parent2Ploidy));
            double denovoProbability = canvasSegments[probandId].CopyNumber > probandPloidy ?
                1 - deNovoGainMarginalLikelihood / (jointLikelihoods.TotalMarginalLikelihood - deNovoLossMarginalLikelihood) :
                1 - deNovoLossMarginalLikelihood / (jointLikelihoods.TotalMarginalLikelihood - deNovoGainMarginalLikelihood);
            // likelihood of proband genotype != ALT given "copyNumberGenotypes" configuration in pedigree with Mendelian conflict 
            return -10.0 * Math.Log10(Math.Max(denovoProbability, q60));
        }

        public static bool IsSharedCnv(ISampleMap<Genotype> copyNumberGenotypes, ISampleMap<CanvasSegment> canvasSegments, ISampleMap<SampleMetrics> samplesInfo, List<SampleId> parentIDs,
            SampleId probandId, int maximumCopyNumber)
        {
            var proband = copyNumberGenotypes[probandId];
            var parent1 = copyNumberGenotypes[parentIDs.First()];
            var parent2 = copyNumberGenotypes[parentIDs.Last()];

            if (proband.PhasedGenotype == null)
                return IsSharedCnv(canvasSegments, samplesInfo, parentIDs, probandId, maximumCopyNumber);

            return (proband.PhasedGenotype.ContainsSharedAlleleA(parent1.PhasedGenotype) &&
                    proband.PhasedGenotype.ContainsSharedAlleleB(parent2.PhasedGenotype)) ||
                   (proband.PhasedGenotype.ContainsSharedAlleleA(parent2.PhasedGenotype) &&
                    proband.PhasedGenotype.ContainsSharedAlleleB(parent1.PhasedGenotype));
        }

        /// <summary>
        /// identify common variants using total CN calls within a pedigree obtained with coverage information only 
        /// </summary>
        /// <param name="canvasSegments"></param>   
        /// <param name="samplesInfo"></param>
        /// <param name="parentIDs"></param>
        /// <param name="probandId"></param>
        /// <param name="maximumCopyNumber"></param>
        /// <returns></returns>
        public static bool IsSharedCnv(ISampleMap<CanvasSegment> canvasSegments, ISampleMap<SampleMetrics> samplesInfo, List<SampleId> parentIDs,
            SampleId probandId, int maximumCopyNumber)
        {
            int parent1CopyNumber = Math.Min(canvasSegments[parentIDs.First()].CopyNumber, maximumCopyNumber - 1);
            int parent2CopyNumber = Math.Min(canvasSegments[parentIDs.Last()].CopyNumber, maximumCopyNumber - 1);
            int probandCopyNumber = Math.Min(canvasSegments[probandId].CopyNumber, maximumCopyNumber - 1);
            var parent1Segment = canvasSegments[parentIDs.First()];
            var parent2Segment = canvasSegments[parentIDs.Last()];
            var probandSegment = canvasSegments[probandId];
            int parent1Ploidy = samplesInfo[parentIDs.First()].GetPloidy(parent1Segment);
            int parent2Ploidy = samplesInfo[parentIDs.Last()].GetPloidy(parent2Segment);
            int probandPloidy = samplesInfo[probandId].GetPloidy(probandSegment);
            // Use the following logic: if the proband has fewer copies than expected (from ploidy) but both parents have at least the expected number of copies OR the 
            // proband has more copies than expected but both parents have no more than the expected number of copies, 
            // then it is not a 'common CNV' (i.e.it could be de novo); otherwise, it is common
            return !(parent1CopyNumber <= parent1Ploidy && parent2CopyNumber <= parent2Ploidy && probandCopyNumber > probandPloidy ||
                     parent1CopyNumber >= parent1Ploidy && parent2CopyNumber >= parent2Ploidy && probandCopyNumber < probandPloidy);
        }
    }
}


